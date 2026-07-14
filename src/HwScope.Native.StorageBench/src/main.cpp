#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <numeric>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {

constexpr int kProtocolVersion = 1;
constexpr std::string_view kWorkerVersion = "0.1.0";
constexpr std::uint64_t kMiB = 1024ULL * 1024ULL;
constexpr DWORD kCancelPollMs = 250;

std::atomic_bool g_cancel_requested = false;

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;

    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(std::exchange(other.value, INVALID_HANDLE_VALUE)) {}
    Handle& operator=(Handle&& other) noexcept {
        if (this != &other) {
            reset();
            value = std::exchange(other.value, INVALID_HANDLE_VALUE);
        }
        return *this;
    }
    ~Handle() { reset(); }

    void reset(HANDLE next = INVALID_HANDLE_VALUE) noexcept {
        if (value != INVALID_HANDLE_VALUE && value != nullptr) {
            CloseHandle(value);
        }
        value = next;
    }

    explicit operator bool() const noexcept { return value != INVALID_HANDLE_VALUE && value != nullptr; }
};

struct AlignedBuffer {
    std::uint8_t* data = nullptr;

    AlignedBuffer() = default;
    AlignedBuffer(std::size_t bytes, std::size_t alignment) {
        data = static_cast<std::uint8_t*>(_aligned_malloc(bytes, alignment));
        if (data == nullptr) {
            throw std::bad_alloc();
        }
    }
    AlignedBuffer(const AlignedBuffer&) = delete;
    AlignedBuffer& operator=(const AlignedBuffer&) = delete;
    AlignedBuffer(AlignedBuffer&& other) noexcept : data(std::exchange(other.data, nullptr)) {}
    AlignedBuffer& operator=(AlignedBuffer&& other) noexcept {
        if (this != &other) {
            _aligned_free(data);
            data = std::exchange(other.data, nullptr);
        }
        return *this;
    }
    ~AlignedBuffer() { _aligned_free(data); }
};

enum class CacheMode { device, buffered };
enum class ColumnMode { read_only, read_write, read_write_mix };
enum class Operation { read, write, mix };

struct Options {
    std::wstring path;
    std::wstring expected_volume_guid;
    std::string session_id;
    std::uint64_t file_size_bytes = 0;
    std::uint64_t write_budget_bytes = 0;
    int runs = 0;
    int warmup_passes = 0;
    int mix_read_percent = 70;
    int alignment_bytes = 4096;
    int expected_protocol_version = 0;
    CacheMode cache_mode = CacheMode::device;
    ColumnMode columns = ColumnMode::read_write;
    std::vector<std::string> workloads;
    bool progress_json = false;
};

struct Workload {
    std::string id;
    std::string display_name;
    DWORD block_size = 0;
    int queue_depth = 0;
    bool random_access = false;
};

struct Aggregate {
    double median = 0;
    double min = 0;
    double max = 0;
    double mean = 0;
    double stddev = 0;
    double cv = 0;
};

struct Latency {
    double mean_microseconds = 0;
    double p50_microseconds = 0;
    double p95_microseconds = 0;
    double p99_microseconds = 0;
    double maximum_microseconds = 0;
};

struct Sample {
    int index = 0;
    double throughput_mb_s = 0;
    double iops = 0;
    Latency latency;
    std::uint64_t bytes_read = 0;
    std::uint64_t bytes_written = 0;
    double elapsed_ms = 0;
};

struct Metric {
    std::vector<Sample> samples;
    std::vector<double> all_latencies;
    Aggregate throughput;
    Aggregate iops;
    Latency latency;
    std::uint64_t logical_bytes_read = 0;
    std::uint64_t logical_bytes_written = 0;
};

struct RowResult {
    Workload workload;
    bool has_read = false;
    bool has_write = false;
    bool has_mix = false;
    Metric read;
    Metric write;
    Metric mix;
};

struct Counters {
    std::uint64_t bytes_read = 0;
    std::uint64_t bytes_written = 0;
    std::uint64_t bytes_write_submitted = 0;
};

struct Cleanup {
    bool attempted = false;
    bool deleted = false;
    DWORD error = ERROR_SUCCESS;
};

class CanceledError final : public std::runtime_error {
public:
    CanceledError() : std::runtime_error("benchmark canceled") {}
};

std::string narrow_ascii(const std::wstring& value) {
    std::string result;
    result.reserve(value.size());
    for (const auto character : value) {
        if (character > 0x7f) {
            throw std::invalid_argument("option name must be ASCII");
        }
        result.push_back(static_cast<char>(character));
    }
    return result;
}

std::uint64_t parse_u64(std::wstring_view value, std::wstring_view name) {
    std::size_t parsed = 0;
    const auto number = std::stoull(std::wstring(value), &parsed, 10);
    if (parsed != value.size()) {
        throw std::invalid_argument(narrow_ascii(std::wstring(name)) + " must be an integer");
    }
    return number;
}

int parse_int(std::wstring_view value, std::wstring_view name) {
    const auto number = parse_u64(value, name);
    if (number > static_cast<std::uint64_t>(std::numeric_limits<int>::max())) {
        throw std::out_of_range(narrow_ascii(std::wstring(name)) + " is too large");
    }
    return static_cast<int>(number);
}

Options parse_args(int argc, wchar_t** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        const std::wstring_view arg(argv[i]);
        auto require_value = [&](std::wstring_view name) -> std::wstring_view {
            if (i + 1 >= argc) {
                throw std::invalid_argument(narrow_ascii(std::wstring(name)) + " requires a value");
            }
            return argv[++i];
        };

        if (arg == L"--path") {
            options.path = require_value(arg);
        } else if (arg == L"--expected-volume-guid") {
            options.expected_volume_guid = require_value(arg);
        } else if (arg == L"--session-id") {
            options.session_id = narrow_ascii(std::wstring(require_value(arg)));
        } else if (arg == L"--file-size-bytes") {
            options.file_size_bytes = parse_u64(require_value(arg), arg);
        } else if (arg == L"--write-budget-bytes") {
            options.write_budget_bytes = parse_u64(require_value(arg), arg);
        } else if (arg == L"--runs") {
            options.runs = parse_int(require_value(arg), arg);
        } else if (arg == L"--warmup-passes") {
            options.warmup_passes = parse_int(require_value(arg), arg);
        } else if (arg == L"--mix-read-percent") {
            options.mix_read_percent = parse_int(require_value(arg), arg);
        } else if (arg == L"--alignment-bytes") {
            options.alignment_bytes = parse_int(require_value(arg), arg);
        } else if (arg == L"--expected-protocol-version") {
            options.expected_protocol_version = parse_int(require_value(arg), arg);
        } else if (arg == L"--cache-mode") {
            const auto value = narrow_ascii(std::wstring(require_value(arg)));
            if (value == "device") {
                options.cache_mode = CacheMode::device;
            } else if (value == "buffered") {
                options.cache_mode = CacheMode::buffered;
            } else {
                throw std::invalid_argument("unknown cache mode");
            }
        } else if (arg == L"--columns") {
            const auto value = narrow_ascii(std::wstring(require_value(arg)));
            if (value == "read-only") {
                options.columns = ColumnMode::read_only;
            } else if (value == "read-write") {
                options.columns = ColumnMode::read_write;
            } else if (value == "read-write-mix") {
                options.columns = ColumnMode::read_write_mix;
            } else {
                throw std::invalid_argument("unknown column mode");
            }
        } else if (arg == L"--workload") {
            options.workloads.push_back(narrow_ascii(std::wstring(require_value(arg))));
        } else if (arg == L"--progress-json") {
            options.progress_json = true;
        } else {
            throw std::invalid_argument("unknown argument: " + narrow_ascii(std::wstring(arg)));
        }
    }

    if (options.path.empty() || options.session_id.empty() || options.expected_volume_guid.empty()) {
        throw std::invalid_argument("--path, --session-id, and --expected-volume-guid are required");
    }
    if (options.file_size_bytes < 64 * kMiB || options.file_size_bytes > 8ULL * 1024ULL * kMiB) {
        throw std::invalid_argument("file size must be between 64 MiB and 8 GiB");
    }
    if (options.runs < 1 || options.runs > 9 || options.warmup_passes < 0 || options.warmup_passes > 1) {
        throw std::invalid_argument("invalid run or warmup count");
    }
    if (options.write_budget_bytes < options.file_size_bytes) {
        throw std::invalid_argument("write budget is smaller than file initialization");
    }
    if (options.mix_read_percent < 1 || options.mix_read_percent > 99) {
        throw std::invalid_argument("mix read percent must be between 1 and 99");
    }
    if (options.alignment_bytes <= 0 || (options.alignment_bytes & (options.alignment_bytes - 1)) != 0) {
        throw std::invalid_argument("alignment must be a positive power of two");
    }
    if (options.expected_protocol_version != 0 && options.expected_protocol_version != kProtocolVersion) {
        throw std::runtime_error("protocol version mismatch");
    }
    if (options.workloads.empty()) {
        options.workloads = {"seq1m-q8t1", "seq1m-q1t1", "rnd4k-q32t1", "rnd4k-q1t1"};
    }
    return options;
}

Workload get_workload(const std::string& id) {
    if (id == "seq1m-q8t1") return {id, "SEQ1M Q8T1", static_cast<DWORD>(kMiB), 8, false};
    if (id == "seq1m-q1t1") return {id, "SEQ1M Q1T1", static_cast<DWORD>(kMiB), 1, false};
    if (id == "rnd4k-q32t1") return {id, "RND4K Q32T1", 4096, 32, true};
    if (id == "rnd4k-q1t1") return {id, "RND4K Q1T1", 4096, 1, true};
    throw std::invalid_argument("unknown workload: " + id);
}

std::string operation_name(Operation operation) {
    switch (operation) {
        case Operation::read: return "read";
        case Operation::write: return "write";
        case Operation::mix: return "mix";
    }
    return "unknown";
}

std::vector<Operation> operations_for(ColumnMode columns) {
    if (columns == ColumnMode::read_only) return {Operation::read};
    if (columns == ColumnMode::read_write) return {Operation::read, Operation::write};
    return {Operation::read, Operation::write, Operation::mix};
}

void throw_last_error(std::string_view operation) {
    throw std::runtime_error(std::string(operation) + " failed with Win32 error " + std::to_string(GetLastError()));
}

void check_canceled() {
    if (g_cancel_requested.load(std::memory_order_relaxed)) {
        throw CanceledError();
    }
}

std::uint64_t xorshift64(std::uint64_t& state) {
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

void fill_pattern(std::uint8_t* data, std::size_t bytes, std::uint64_t seed) {
    auto state = seed == 0 ? 0x9e3779b97f4a7c15ULL : seed;
    for (std::size_t offset = 0; offset < bytes; offset += sizeof(std::uint64_t)) {
        const auto value = xorshift64(state);
        const auto remaining = std::min(sizeof(value), bytes - offset);
        std::memcpy(data + offset, &value, remaining);
    }
}

void validate_opened_volume(HANDLE file, const Options& options) {
    std::vector<wchar_t> buffer(512);
    DWORD length = GetFinalPathNameByHandleW(file, buffer.data(), static_cast<DWORD>(buffer.size()), VOLUME_NAME_GUID);
    if (length == 0) {
        throw_last_error("GetFinalPathNameByHandleW");
    }
    if (length >= buffer.size()) {
        buffer.resize(static_cast<std::size_t>(length) + 1);
        length = GetFinalPathNameByHandleW(file, buffer.data(), static_cast<DWORD>(buffer.size()), VOLUME_NAME_GUID);
        if (length == 0 || length >= buffer.size()) {
            throw_last_error("GetFinalPathNameByHandleW");
        }
    }

    std::wstring expected = options.expected_volume_guid;
    if (expected.back() != L'\\') expected.push_back(L'\\');
    const std::wstring actual(buffer.data(), length);
    if (actual.size() < expected.size() || _wcsnicmp(actual.c_str(), expected.c_str(), expected.size()) != 0) {
        throw std::runtime_error("opened test file is not on the expected volume");
    }
}

double percentile_sorted(const std::vector<double>& sorted, double percentile) {
    if (sorted.empty()) return 0;
    const auto position = percentile * static_cast<double>(sorted.size() - 1);
    const auto lower = static_cast<std::size_t>(position);
    const auto upper = std::min(lower + 1, sorted.size() - 1);
    const auto fraction = position - static_cast<double>(lower);
    return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
}

Latency calculate_latency(std::vector<double> values) {
    Latency result;
    if (values.empty()) return result;
    std::sort(values.begin(), values.end());
    result.mean_microseconds = std::accumulate(values.begin(), values.end(), 0.0) / static_cast<double>(values.size());
    result.p50_microseconds = percentile_sorted(values, 0.50);
    result.p95_microseconds = percentile_sorted(values, 0.95);
    result.p99_microseconds = percentile_sorted(values, 0.99);
    result.maximum_microseconds = values.back();
    return result;
}

Aggregate calculate_aggregate(std::vector<double> values) {
    Aggregate result;
    if (values.empty()) return result;
    std::sort(values.begin(), values.end());
    result.median = percentile_sorted(values, 0.50);
    result.min = values.front();
    result.max = values.back();
    result.mean = std::accumulate(values.begin(), values.end(), 0.0) / static_cast<double>(values.size());
    double variance = 0;
    for (const auto value : values) {
        const auto delta = value - result.mean;
        variance += delta * delta;
    }
    result.stddev = std::sqrt(variance / static_cast<double>(values.size()));
    result.cv = result.mean == 0 ? 0 : result.stddev / result.mean;
    return result;
}

std::atomic<std::uint64_t> g_sequence = 0;

void emit_started(const Options& options) {
    if (!options.progress_json) return;
    std::cout << "{\"type\":\"started\",\"protocol_version\":" << kProtocolVersion
              << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
              << ",\"phase\":\"preflight\"}\n" << std::flush;
}

void emit_phase(const Options& options, std::string_view phase, std::uint64_t completed = 0, std::uint64_t planned = 0) {
    if (!options.progress_json) return;
    std::cout << "{\"type\":\"phase\",\"protocol_version\":" << kProtocolVersion
              << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
              << ",\"phase\":\"" << phase << "\"";
    if (planned > 0) {
        std::cout << ",\"completed_bytes\":" << completed << ",\"planned_bytes\":" << planned;
    }
    std::cout << "}\n" << std::flush;
}

void emit_workload(const Options& options, std::string_view type, const Workload& workload, Operation operation) {
    if (!options.progress_json) return;
    std::cout << "{\"type\":\"" << type << "\",\"protocol_version\":" << kProtocolVersion
              << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
              << ",\"phase\":\"running\",\"workload_id\":\"" << workload.id
              << "\",\"operation\":\"" << operation_name(operation) << "\"}\n" << std::flush;
}

void emit_progress(const Options& options, const Workload& workload, Operation operation, int sample_index,
                   std::uint64_t completed, std::uint64_t planned) {
    if (!options.progress_json) return;
    std::cout << "{\"type\":\"workload_progress\",\"protocol_version\":" << kProtocolVersion
              << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
              << ",\"phase\":\"running\",\"workload_id\":\"" << workload.id
              << "\",\"operation\":\"" << operation_name(operation)
              << "\",\"sample_index\":" << sample_index << ",\"sample_count\":" << options.runs
              << ",\"completed_bytes\":" << completed << ",\"planned_bytes\":" << planned << "}\n" << std::flush;
}

void emit_sample(const Options& options, const Workload& workload, Operation operation, const Sample& sample) {
    if (!options.progress_json) return;
    std::cout << std::fixed << std::setprecision(4)
              << "{\"type\":\"sample_completed\",\"protocol_version\":" << kProtocolVersion
              << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
              << ",\"phase\":\"running\",\"workload_id\":\"" << workload.id
              << "\",\"operation\":\"" << operation_name(operation)
              << "\",\"sample_index\":" << sample.index << ",\"sample_count\":" << options.runs
              << ",\"throughput_mb_s\":" << sample.throughput_mb_s << ",\"iops\":" << sample.iops
              << ",\"p95_microseconds\":" << sample.latency.p95_microseconds << "}\n" << std::flush;
}

std::uint64_t prepare_file(const Options& options) {
    emit_phase(options, "preparing", 0, options.file_size_bytes);
    DWORD flags = FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_SEQUENTIAL_SCAN;
    if (options.cache_mode == CacheMode::device) flags |= FILE_FLAG_NO_BUFFERING;
    Handle file(CreateFileW(options.path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_NEW, flags, nullptr));
    if (!file) throw_last_error("CreateFile(CREATE_NEW)");
    validate_opened_volume(file.value, options);

    LARGE_INTEGER size;
    size.QuadPart = static_cast<LONGLONG>(options.file_size_bytes);
    if (!SetFilePointerEx(file.value, size, nullptr, FILE_BEGIN) || !SetEndOfFile(file.value)) {
        throw_last_error("SetEndOfFile");
    }
    LARGE_INTEGER start{};
    if (!SetFilePointerEx(file.value, start, nullptr, FILE_BEGIN)) throw_last_error("SetFilePointerEx");

    const auto chunk_size = static_cast<std::size_t>(kMiB);
    AlignedBuffer buffer(chunk_size, static_cast<std::size_t>(options.alignment_bytes));
    std::uint64_t written = 0;
    std::uint64_t last_report = 0;
    while (written < options.file_size_bytes) {
        check_canceled();
        const auto chunk = static_cast<DWORD>(std::min<std::uint64_t>(chunk_size, options.file_size_bytes - written));
        if (written + chunk > options.write_budget_bytes) throw std::runtime_error("write budget exceeded during preparation");
        fill_pattern(buffer.data, chunk, 0x485753434F5045ULL ^ written);
        DWORD completed = 0;
        if (!WriteFile(file.value, buffer.data, chunk, &completed, nullptr) || completed != chunk) {
            throw_last_error("WriteFile(preparation)");
        }
        written += completed;
        if (written - last_report >= 64 * kMiB || written == options.file_size_bytes) {
            emit_phase(options, "preparing", written, options.file_size_bytes);
            last_report = written;
        }
    }
    if (!FlushFileBuffers(file.value)) throw_last_error("FlushFileBuffers(preparation)");
    return written;
}

struct IoSlot {
    OVERLAPPED overlapped{};
    AlignedBuffer buffer;
    LARGE_INTEGER submitted{};
    bool write = false;

    IoSlot(std::size_t bytes, std::size_t alignment) : buffer(bytes, alignment) {}
};

static_assert(offsetof(IoSlot, overlapped) == 0, "OVERLAPPED must remain the first IoSlot member");

std::uint64_t choose_offset(const Workload& workload, std::uint64_t operation_index,
                            std::uint64_t total_blocks, std::uint64_t& random_state) {
    const auto block = workload.random_access ? xorshift64(random_state) % total_blocks : operation_index % total_blocks;
    return block * workload.block_size;
}

bool choose_write(Operation operation, std::uint64_t operation_index, std::uint64_t total_operations,
                  int mix_read_percent, std::uint64_t seed) {
    if (operation == Operation::write) return true;
    if (operation == Operation::read) return false;
    const auto read_operations = total_operations * static_cast<std::uint64_t>(mix_read_percent) / 100ULL;
    const auto permuted = (operation_index * 65537ULL + seed) % total_operations;
    return permuted >= read_operations;
}

Sample run_sample(HANDLE file, HANDLE completion_port, const Options& options, const Workload& workload,
                  Operation operation, int sample_index, std::uint64_t seed, Counters& counters, bool report_progress) {
    const auto total_operations = options.file_size_bytes / workload.block_size;
    const auto total_blocks = total_operations;
    std::vector<std::unique_ptr<IoSlot>> slots;
    slots.reserve(static_cast<std::size_t>(workload.queue_depth));
    for (int i = 0; i < workload.queue_depth; ++i) {
        slots.push_back(std::make_unique<IoSlot>(workload.block_size, static_cast<std::size_t>(options.alignment_bytes)));
        fill_pattern(slots.back()->buffer.data, workload.block_size, seed ^ static_cast<std::uint64_t>(i + 1));
    }

    struct OutstandingIoGuard {
        std::vector<std::unique_ptr<IoSlot>>& slots;
        bool armed = true;
        ~OutstandingIoGuard() {
            if (armed) {
                // Keep OVERLAPPED and buffers alive until process exit if an unexpected I/O error unwinds the sample.
                for (auto& slot : slots) {
                    (void)slot.release();
                }
            }
        }
    } io_guard{slots};

    LARGE_INTEGER frequency{};
    LARGE_INTEGER benchmark_start{};
    if (!QueryPerformanceFrequency(&frequency) || !QueryPerformanceCounter(&benchmark_start)) {
        throw_last_error("QueryPerformanceCounter");
    }

    std::uint64_t submitted = 0;
    std::uint64_t completed_operations = 0;
    std::uint64_t bytes_read = 0;
    std::uint64_t bytes_written = 0;
    std::uint64_t random_state = seed == 0 ? 1 : seed;
    std::uint64_t last_report_bytes = 0;
    std::vector<double> latencies;
    latencies.reserve(static_cast<std::size_t>(total_operations));

    auto submit = [&](IoSlot& slot, std::uint64_t operation_index) {
        std::memset(&slot.overlapped, 0, sizeof(slot.overlapped));
        const auto offset = choose_offset(workload, operation_index, total_blocks, random_state);
        slot.overlapped.Offset = static_cast<DWORD>(offset & 0xffffffffULL);
        slot.overlapped.OffsetHigh = static_cast<DWORD>(offset >> 32);
        slot.write = choose_write(operation, operation_index, total_operations, options.mix_read_percent, seed);
        if (slot.write) {
            if (counters.bytes_write_submitted > options.write_budget_bytes - workload.block_size) {
                throw std::runtime_error("write budget exceeded before I/O submission");
            }
            counters.bytes_write_submitted += workload.block_size;
            std::memcpy(slot.buffer.data, &operation_index, std::min<std::size_t>(sizeof(operation_index), workload.block_size));
        }
        QueryPerformanceCounter(&slot.submitted);
        const BOOL started = slot.write
            ? WriteFile(file, slot.buffer.data, workload.block_size, nullptr, &slot.overlapped)
            : ReadFile(file, slot.buffer.data, workload.block_size, nullptr, &slot.overlapped);
        if (!started && GetLastError() != ERROR_IO_PENDING) throw_last_error(slot.write ? "WriteFile" : "ReadFile");
    };

    const auto initial = std::min<std::uint64_t>(total_operations, static_cast<std::uint64_t>(workload.queue_depth));
    for (std::uint64_t i = 0; i < initial; ++i) {
        submit(*slots[static_cast<std::size_t>(i)], submitted++);
    }
    auto outstanding = initial;
    bool canceling = false;

    auto begin_cancel = [&] {
        if (!canceling) {
            canceling = true;
            if (!CancelIoEx(file, nullptr) && GetLastError() != ERROR_NOT_FOUND) {
                throw_last_error("CancelIoEx");
            }
        }
    };

    while (outstanding > 0) {
        if (g_cancel_requested.load(std::memory_order_relaxed)) {
            begin_cancel();
        }
        DWORD transferred = 0;
        ULONG_PTR key = 0;
        OVERLAPPED* completed_overlapped = nullptr;
        const BOOL ok = GetQueuedCompletionStatus(completion_port, &transferred, &key, &completed_overlapped, kCancelPollMs);
        (void)key;
        if (!ok && completed_overlapped == nullptr) {
            if (GetLastError() == WAIT_TIMEOUT) {
                if (g_cancel_requested.load(std::memory_order_relaxed)) {
                    begin_cancel();
                }
                continue;
            }
            throw_last_error("GetQueuedCompletionStatus");
        }
        if (completed_overlapped == nullptr) throw std::runtime_error("completion port returned no OVERLAPPED");
        if (!ok) {
            if (GetLastError() == ERROR_OPERATION_ABORTED && canceling) {
                --outstanding;
                if (outstanding == 0) {
                    io_guard.armed = false;
                    throw CanceledError();
                }
                continue;
            }
            throw_last_error("asynchronous file I/O");
        }
        if (transferred != workload.block_size) throw std::runtime_error("short asynchronous file I/O");

        auto* slot = reinterpret_cast<IoSlot*>(completed_overlapped);
        LARGE_INTEGER finished{};
        QueryPerformanceCounter(&finished);
        latencies.push_back(static_cast<double>(finished.QuadPart - slot->submitted.QuadPart) * 1'000'000.0
            / static_cast<double>(frequency.QuadPart));
        if (slot->write) {
            bytes_written += transferred;
            counters.bytes_written += transferred;
        } else {
            bytes_read += transferred;
            counters.bytes_read += transferred;
        }
        ++completed_operations;
        --outstanding;

        if (!canceling && submitted < total_operations) {
            if (g_cancel_requested.load(std::memory_order_relaxed)) {
                begin_cancel();
            } else {
                submit(*slot, submitted++);
                ++outstanding;
            }
        }

        const auto completed_bytes = completed_operations * workload.block_size;
        if (report_progress && (completed_bytes - last_report_bytes >= 64 * kMiB || completed_operations == total_operations)) {
            emit_progress(options, workload, operation, sample_index, completed_bytes, options.file_size_bytes);
            last_report_bytes = completed_bytes;
        }

        if (canceling && outstanding == 0) {
            io_guard.armed = false;
            throw CanceledError();
        }
    }

    io_guard.armed = false;

    LARGE_INTEGER benchmark_end{};
    QueryPerformanceCounter(&benchmark_end);
    const auto seconds = static_cast<double>(benchmark_end.QuadPart - benchmark_start.QuadPart) / static_cast<double>(frequency.QuadPart);
    Sample sample;
    sample.index = sample_index;
    sample.elapsed_ms = seconds * 1000.0;
    sample.bytes_read = bytes_read;
    sample.bytes_written = bytes_written;
    sample.throughput_mb_s = static_cast<double>(bytes_read + bytes_written) / 1'000'000.0 / seconds;
    sample.iops = static_cast<double>(completed_operations) / seconds;
    sample.latency = calculate_latency(latencies);
    return sample;
}

Metric run_metric(HANDLE file, HANDLE completion_port, const Options& options, const Workload& workload,
                  Operation operation, std::uint64_t seed, Counters& counters) {
    emit_workload(options, "workload_started", workload, operation);
    Metric metric;
    for (int warmup = 0; warmup < options.warmup_passes; ++warmup) {
        (void)run_sample(file, completion_port, options, workload, operation, 0, seed ^ 0xfeed0000ULL, counters, false);
        if (operation != Operation::read && !FlushFileBuffers(file)) throw_last_error("FlushFileBuffers(warmup)");
    }
    for (int run = 1; run <= options.runs; ++run) {
        auto sample = run_sample(file, completion_port, options, workload, operation, run,
            seed ^ static_cast<std::uint64_t>(run), counters, true);
        if (operation != Operation::read && !FlushFileBuffers(file)) throw_last_error("FlushFileBuffers(sample)");
        metric.logical_bytes_read += sample.bytes_read;
        metric.logical_bytes_written += sample.bytes_written;
        metric.samples.push_back(sample);
        emit_sample(options, workload, operation, sample);
    }

    std::vector<double> throughput;
    std::vector<double> iops;
    for (const auto& sample : metric.samples) {
        throughput.push_back(sample.throughput_mb_s);
        iops.push_back(sample.iops);
        metric.all_latencies.push_back(sample.latency.p50_microseconds);
        metric.all_latencies.push_back(sample.latency.p95_microseconds);
        metric.all_latencies.push_back(sample.latency.p99_microseconds);
    }
    metric.throughput = calculate_aggregate(std::move(throughput));
    metric.iops = calculate_aggregate(std::move(iops));

    std::vector<double> means;
    std::vector<double> p50;
    std::vector<double> p95;
    std::vector<double> p99;
    for (const auto& sample : metric.samples) {
        means.push_back(sample.latency.mean_microseconds);
        p50.push_back(sample.latency.p50_microseconds);
        p95.push_back(sample.latency.p95_microseconds);
        p99.push_back(sample.latency.p99_microseconds);
    }
    metric.latency.mean_microseconds = calculate_aggregate(means).mean;
    metric.latency.p50_microseconds = calculate_aggregate(p50).median;
    metric.latency.p95_microseconds = calculate_aggregate(p95).median;
    metric.latency.p99_microseconds = calculate_aggregate(p99).median;
    metric.latency.maximum_microseconds = 0;
    for (const auto& sample : metric.samples) {
        metric.latency.maximum_microseconds = (std::max)(metric.latency.maximum_microseconds, sample.latency.maximum_microseconds);
    }
    emit_workload(options, "workload_completed", workload, operation);
    return metric;
}

std::vector<RowResult> run_benchmark(const Options& options, Counters& counters) {
    DWORD flags = FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_OVERLAPPED;
    if (options.cache_mode == CacheMode::device) flags |= FILE_FLAG_NO_BUFFERING;
    Handle file(CreateFileW(options.path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, flags, nullptr));
    if (!file) throw_last_error("CreateFile(OPEN_EXISTING)");
    Handle completion(CreateIoCompletionPort(file.value, nullptr, 0, 0));
    if (!completion) throw_last_error("CreateIoCompletionPort");

    std::vector<RowResult> rows;
    std::uint64_t seed = 0x485753434F5045ULL;
    for (const auto& workload_id : options.workloads) {
        RowResult row;
        row.workload = get_workload(workload_id);
        if (options.file_size_bytes % row.workload.block_size != 0
            || (options.cache_mode == CacheMode::device && row.workload.block_size % options.alignment_bytes != 0)) {
            throw std::invalid_argument("workload block size does not satisfy file size/alignment");
        }
        for (const auto operation : operations_for(options.columns)) {
            auto metric = run_metric(file.value, completion.value, options, row.workload, operation, seed++, counters);
            if (operation == Operation::read) {
                row.has_read = true;
                row.read = std::move(metric);
            } else if (operation == Operation::write) {
                row.has_write = true;
                row.write = std::move(metric);
            } else {
                row.has_mix = true;
                row.mix = std::move(metric);
            }
        }
        rows.push_back(std::move(row));
    }
    return rows;
}

Cleanup cleanup_file(const Options& options) {
    Cleanup cleanup;
    cleanup.attempted = true;
    emit_phase(options, "cleanup");
    if (DeleteFileW(options.path.c_str())) {
        cleanup.deleted = true;
        return cleanup;
    }
    cleanup.error = GetLastError();
    cleanup.deleted = cleanup.error == ERROR_FILE_NOT_FOUND;
    return cleanup;
}

void print_aggregate(std::ostream& os, const Aggregate& value) {
    os << "{\"median\":" << value.median << ",\"min\":" << value.min << ",\"max\":" << value.max
       << ",\"mean\":" << value.mean << ",\"std_dev\":" << value.stddev << ",\"cv\":" << value.cv << '}';
}

void print_latency(std::ostream& os, const Latency& value) {
    os << "{\"mean_microseconds\":" << value.mean_microseconds << ",\"p50_microseconds\":" << value.p50_microseconds
       << ",\"p95_microseconds\":" << value.p95_microseconds << ",\"p99_microseconds\":" << value.p99_microseconds
       << ",\"maximum_microseconds\":" << value.maximum_microseconds << '}';
}

void print_metric(std::ostream& os, const Metric& metric) {
    os << "{\"unit\":\"mb_s\",\"samples\":[";
    for (std::size_t i = 0; i < metric.samples.size(); ++i) {
        if (i > 0) os << ',';
        const auto& sample = metric.samples[i];
        os << "{\"index\":" << sample.index << ",\"throughput_mb_s\":" << sample.throughput_mb_s
           << ",\"iops\":" << sample.iops << ",\"latency\":";
        print_latency(os, sample.latency);
        os << ",\"bytes_read\":" << sample.bytes_read << ",\"bytes_written\":" << sample.bytes_written
           << ",\"elapsed_ms\":" << sample.elapsed_ms << '}';
    }
    os << "],\"throughput\":";
    print_aggregate(os, metric.throughput);
    os << ",\"iops\":";
    print_aggregate(os, metric.iops);
    os << ",\"latency\":";
    print_latency(os, metric.latency);
    os << ",\"logical_bytes_read\":" << metric.logical_bytes_read
       << ",\"logical_bytes_written\":" << metric.logical_bytes_written << '}';
}

void print_result(const Options& options, const std::vector<RowResult>& rows, const Counters& counters,
                  const Cleanup& cleanup, double elapsed_ms) {
    std::cout << std::fixed << std::setprecision(6)
              << "{\"type\":\"result\",\"session_id\":\"" << options.session_id
              << "\",\"worker_version\":\"" << kWorkerVersion << "\",\"protocol_version\":" << kProtocolVersion
              << ",\"elapsed_ms\":" << elapsed_ms << ",\"file_size_bytes\":" << options.file_size_bytes
              << ",\"logical_bytes_read\":" << counters.bytes_read
              << ",\"logical_bytes_written\":" << counters.bytes_written
              << ",\"cache_mode\":\"" << (options.cache_mode == CacheMode::device ? "device" : "buffered")
              << "\",\"rows\":{";
    for (std::size_t i = 0; i < rows.size(); ++i) {
        if (i > 0) std::cout << ',';
        const auto& row = rows[i];
        std::cout << '\"' << row.workload.id << "\":{\"id\":\"" << row.workload.id
                  << "\",\"display_name\":\"" << row.workload.display_name
                  << "\",\"block_size_bytes\":" << row.workload.block_size
                  << ",\"queue_depth\":" << row.workload.queue_depth << ",\"threads\":1,\"read\":";
        if (row.has_read) print_metric(std::cout, row.read); else std::cout << "null";
        std::cout << ",\"write\":";
        if (row.has_write) print_metric(std::cout, row.write); else std::cout << "null";
        std::cout << ",\"mix\":";
        if (row.has_mix) print_metric(std::cout, row.mix); else std::cout << "null";
        std::cout << '}';
    }
    std::cout << "},\"cleanup\":{\"attempted\":" << (cleanup.attempted ? "true" : "false")
              << ",\"deleted\":" << (cleanup.deleted ? "true" : "false")
              << ",\"status\":\"" << (cleanup.deleted ? "deleted" : "deleteFailed") << "\",\"native_error_code\":";
    if (cleanup.error == ERROR_SUCCESS) std::cout << "null"; else std::cout << cleanup.error;
    std::cout << "}}\n" << std::flush;
    if (options.progress_json) {
        std::cout << "{\"type\":\"completed\",\"protocol_version\":" << kProtocolVersion
                  << ",\"session_id\":\"" << options.session_id << "\",\"sequence\":" << ++g_sequence
                  << ",\"phase\":\"completed\"}\n" << std::flush;
    }
}

void start_cancel_listener() {
    std::thread([] {
        std::string line;
        while (std::getline(std::cin, line)) {
            if (line == "cancel") {
                g_cancel_requested.store(true, std::memory_order_relaxed);
                return;
            }
        }
    }).detach();
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    Options options;
    bool parsed = false;
    try {
        options = parse_args(argc, argv);
        parsed = true;
        start_cancel_listener();
        emit_started(options);
        const auto started = std::chrono::steady_clock::now();
        Counters counters;
        counters.bytes_written = prepare_file(options);
        counters.bytes_write_submitted = counters.bytes_written;
        auto rows = run_benchmark(options, counters);
        auto cleanup = cleanup_file(options);
        const auto finished = std::chrono::steady_clock::now();
        const auto elapsed_ms = std::chrono::duration<double, std::milli>(finished - started).count();
        print_result(options, rows, counters, cleanup, elapsed_ms);
        return 0;
    } catch (const CanceledError&) {
        if (parsed) {
            (void)cleanup_file(options);
        }
        std::cerr << "canceled\n";
        return 2;
    } catch (const std::exception& ex) {
        if (parsed) {
            (void)cleanup_file(options);
        }
        std::cerr << "error: " << ex.what() << '\n';
        return 1;
    }
}
