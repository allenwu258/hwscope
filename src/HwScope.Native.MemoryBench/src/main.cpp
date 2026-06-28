#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <numeric>
#include <random>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#if defined(_WIN32)
#define NOMINMAX
#include <malloc.h>
#include <windows.h>
#endif

namespace {

constexpr std::size_t kAlignment = 64;
constexpr std::size_t kMiB = 1024ull * 1024ull;
constexpr std::uint64_t kDefaultSizeMiB = 512;
constexpr int kDefaultIterations = 7;
constexpr std::uint64_t kDefaultLatencySteps = 20'000'000;
constexpr std::string_view kWorkerVersion = "0.2.0";
constexpr int kProtocolVersion = 2;

volatile std::uint64_t g_sink = 0;

struct Options {
    std::uint64_t size_mib = kDefaultSizeMiB;
    int iterations = kDefaultIterations;
    std::uint64_t latency_steps = kDefaultLatencySteps;
    bool csv = false;
    bool json = false;
    bool progress_json = false;
};

struct AlignedFree {
    void operator()(std::uint8_t* ptr) const noexcept {
#if defined(_WIN32)
        _aligned_free(ptr);
#else
        std::free(ptr);
#endif
    }
};

using AlignedBuffer = std::unique_ptr<std::uint8_t, AlignedFree>;

struct BenchResult {
    double read_mib_s = 0.0;
    double write_mib_s = 0.0;
    double copy_mib_s = 0.0;
    double latency_ns = 0.0;
    std::vector<double> read_samples;
    std::vector<double> write_samples;
    std::vector<double> copy_samples;
    std::vector<double> latency_samples;
    double elapsed_ms = 0.0;
};

struct Aggregate {
    double median = 0.0;
    double min = 0.0;
    double max = 0.0;
    double mean = 0.0;
    double stddev = 0.0;
    double cv = 0.0;
};

void print_usage() {
    std::cout
        << "Usage: membench [--size-mib N] [--iterations N] [--latency-steps N] [--csv] [--json] [--progress-json]\n"
        << "\n"
        << "Defaults:\n"
        << "  --size-mib " << kDefaultSizeMiB << "\n"
        << "  --iterations " << kDefaultIterations << "\n"
        << "  --latency-steps " << kDefaultLatencySteps << "\n";
}

std::uint64_t parse_u64(std::string_view value, std::string_view name) {
    std::size_t consumed = 0;
    std::uint64_t parsed = 0;
    try {
        parsed = std::stoull(std::string(value), &consumed, 10);
    } catch (const std::exception&) {
        throw std::invalid_argument("Invalid integer for " + std::string(name));
    }

    if (consumed != value.size()) {
        throw std::invalid_argument("Invalid integer for " + std::string(name));
    }
    return parsed;
}

Options parse_args(int argc, char** argv) {
    Options options;

    for (int i = 1; i < argc; ++i) {
        const std::string_view arg(argv[i]);

        if (arg == "--help" || arg == "-h") {
            print_usage();
            std::exit(0);
        }
        if (arg == "--csv") {
            options.csv = true;
            continue;
        }
        if (arg == "--json") {
            options.json = true;
            continue;
        }
        if (arg == "--progress-json") {
            options.progress_json = true;
            continue;
        }

        auto require_value = [&](std::string_view name) -> std::string_view {
            if (i + 1 >= argc) {
                throw std::invalid_argument("Missing value for " + std::string(name));
            }
            return argv[++i];
        };

        if (arg == "--size-mib") {
            options.size_mib = parse_u64(require_value(arg), arg);
        } else if (arg == "--iterations") {
            const auto value = parse_u64(require_value(arg), arg);
            if (value == 0 || value > 1000) {
                throw std::invalid_argument("--iterations must be in [1, 1000]");
            }
            options.iterations = static_cast<int>(value);
        } else if (arg == "--latency-steps") {
            options.latency_steps = parse_u64(require_value(arg), arg);
        } else {
            throw std::invalid_argument("Unknown argument: " + std::string(arg));
        }
    }

    if (options.size_mib < 16) {
        throw std::invalid_argument("--size-mib must be at least 16");
    }
    if (options.latency_steps == 0) {
        throw std::invalid_argument("--latency-steps must be greater than 0");
    }
    if ((options.csv ? 1 : 0) + (options.json ? 1 : 0) + (options.progress_json ? 1 : 0) > 1) {
        throw std::invalid_argument("--csv, --json, and --progress-json are mutually exclusive");
    }

    return options;
}

AlignedBuffer make_aligned_buffer(std::size_t bytes) {
#if defined(_WIN32)
    auto* ptr = static_cast<std::uint8_t*>(_aligned_malloc(bytes, kAlignment));
    if (!ptr) {
        throw std::bad_alloc();
    }
    return AlignedBuffer(ptr);
#else
    void* raw = nullptr;
    if (posix_memalign(&raw, kAlignment, bytes) != 0) {
        throw std::bad_alloc();
    }
    return AlignedBuffer(static_cast<std::uint8_t*>(raw));
#endif
}

#if defined(_WIN32)
void pin_to_current_cpu() {
    const DWORD cpu = GetCurrentProcessorNumber();
    if (cpu < sizeof(DWORD_PTR) * 8) {
        SetThreadAffinityMask(GetCurrentThread(), DWORD_PTR{1} << cpu);
    }
}
#else
void pin_to_current_cpu() {}
#endif

template <typename Fn>
double elapsed_seconds(Fn&& fn) {
    const auto start = std::chrono::steady_clock::now();
    fn();
    const auto finish = std::chrono::steady_clock::now();
    return std::chrono::duration<double>(finish - start).count();
}

double median(std::vector<double> values) {
    if (values.empty()) {
        return 0.0;
    }
    const auto mid = values.begin() + static_cast<std::ptrdiff_t>(values.size() / 2);
    std::nth_element(values.begin(), mid, values.end());
    return *mid;
}

Aggregate aggregate(const std::vector<double>& values) {
    Aggregate result;
    if (values.empty()) {
        return result;
    }

    result.median = median(values);
    const auto [min_it, max_it] = std::minmax_element(values.begin(), values.end());
    result.min = *min_it;
    result.max = *max_it;
    result.mean = std::accumulate(values.begin(), values.end(), 0.0) / static_cast<double>(values.size());

    double variance = 0.0;
    for (const double value : values) {
        const double delta = value - result.mean;
        variance += delta * delta;
    }

    variance /= static_cast<double>(values.size());
    result.stddev = std::sqrt(variance);
    result.cv = result.mean == 0.0 ? 0.0 : result.stddev / result.mean;
    return result;
}

void touch_pages(std::uint8_t* data, std::size_t bytes) {
    constexpr std::size_t page = 4096;
    for (std::size_t i = 0; i < bytes; i += page) {
        data[i] = static_cast<std::uint8_t>(i);
    }
}

std::vector<double> run_read(std::uint8_t* data, std::size_t bytes, int iterations) {
    constexpr std::size_t unroll = 8;
    constexpr std::size_t stride = sizeof(std::uint64_t) * unroll;
    const auto count = bytes / sizeof(std::uint64_t);
    const auto* words = reinterpret_cast<const std::uint64_t*>(data);
    std::vector<double> samples;
    samples.reserve(static_cast<std::size_t>(iterations));

    for (int round = 0; round < iterations; ++round) {
        std::uint64_t sum0 = 0;
        std::uint64_t sum1 = 0;
        std::uint64_t sum2 = 0;
        std::uint64_t sum3 = 0;
        std::uint64_t sum4 = 0;
        std::uint64_t sum5 = 0;
        std::uint64_t sum6 = 0;
        std::uint64_t sum7 = 0;

        const double seconds = elapsed_seconds([&] {
            std::size_t i = 0;
            for (; i + unroll <= count; i += unroll) {
                sum0 += words[i + 0];
                sum1 += words[i + 1];
                sum2 += words[i + 2];
                sum3 += words[i + 3];
                sum4 += words[i + 4];
                sum5 += words[i + 5];
                sum6 += words[i + 6];
                sum7 += words[i + 7];
            }
            for (; i < count; ++i) {
                sum0 += words[i];
            }
        });

        g_sink ^= sum0 ^ sum1 ^ sum2 ^ sum3 ^ sum4 ^ sum5 ^ sum6 ^ sum7 ^ stride;
        samples.push_back(static_cast<double>(bytes) / seconds / static_cast<double>(kMiB));
    }

    return samples;
}

std::vector<double> run_write(std::uint8_t* data, std::size_t bytes, int iterations) {
    std::vector<double> samples;
    samples.reserve(static_cast<std::size_t>(iterations));

    for (int round = 0; round < iterations; ++round) {
        const auto value = static_cast<int>((round * 37) & 0xff);
        const double seconds = elapsed_seconds([&] {
            std::memset(data, value, bytes);
        });
        g_sink ^= data[static_cast<std::size_t>(round) % bytes];
        samples.push_back(static_cast<double>(bytes) / seconds / static_cast<double>(kMiB));
    }

    return samples;
}

std::vector<double> run_copy(std::uint8_t* dst, const std::uint8_t* src, std::size_t bytes, int iterations) {
    std::vector<double> samples;
    samples.reserve(static_cast<std::size_t>(iterations));

    for (int round = 0; round < iterations; ++round) {
        const double seconds = elapsed_seconds([&] {
            std::memcpy(dst, src, bytes);
        });
        g_sink ^= dst[(static_cast<std::size_t>(round) * 4099) % bytes];
        samples.push_back(static_cast<double>(bytes) / seconds / static_cast<double>(kMiB));
    }

    return samples;
}

std::vector<double> run_latency(std::size_t bytes, std::uint64_t steps, int iterations) {
    const std::size_t node_count = bytes / sizeof(std::uint32_t);
    if (node_count < 2) {
        throw std::invalid_argument("Latency buffer is too small");
    }
    if (node_count > static_cast<std::size_t>(std::numeric_limits<std::uint32_t>::max())) {
        throw std::invalid_argument("Latency buffer is too large for this prototype");
    }

    std::vector<std::uint32_t> order(node_count);
    std::iota(order.begin(), order.end(), 0u);
    std::mt19937 rng(0xC0FFEEu);
    std::shuffle(order.begin(), order.end(), rng);

    std::vector<std::uint32_t> next(node_count);
    for (std::size_t i = 0; i < node_count; ++i) {
        next[order[i]] = order[(i + 1) % node_count];
    }

    volatile const std::uint32_t* chain = next.data();
    std::uint32_t index = order[0];
    const std::uint64_t sample_steps = std::max<std::uint64_t>(1, steps / static_cast<std::uint64_t>(iterations));

    for (std::uint64_t i = 0; i < std::min<std::uint64_t>(steps / 10, 1'000'000); ++i) {
        index = chain[index];
    }

    std::vector<double> samples;
    samples.reserve(static_cast<std::size_t>(iterations));
    for (int round = 0; round < iterations; ++round) {
        const double seconds = elapsed_seconds([&] {
            for (std::uint64_t i = 0; i < sample_steps; ++i) {
                index = chain[index];
            }
        });

        samples.push_back(seconds * 1'000'000'000.0 / static_cast<double>(sample_steps));
    }

    g_sink ^= index;
    return samples;
}

void emit_progress_started(const Options& options) {
    std::cout << "{\"type\":\"started\",\"size_mib\":" << options.size_mib
              << ",\"iterations\":" << options.iterations
              << ",\"latency_steps\":" << options.latency_steps << "}\n"
              << std::flush;
}

void emit_progress_metric(std::string_view metric, double value, std::string_view unit) {
    std::cout << "{\"type\":\"metric\",\"metric\":\"" << metric
              << "\",\"value\":" << std::fixed << std::setprecision(2) << value
              << ",\"unit\":\"" << unit << "\"}\n"
              << std::flush;
}

void emit_progress_completed() {
    std::cout << "{\"type\":\"completed\"}\n" << std::flush;
}

void print_json_string(std::ostream& os, std::string_view value) {
    os << '"';
    for (const char ch : value) {
        switch (ch) {
            case '\\':
                os << "\\\\";
                break;
            case '"':
                os << "\\\"";
                break;
            case '\n':
                os << "\\n";
                break;
            case '\r':
                os << "\\r";
                break;
            case '\t':
                os << "\\t";
                break;
            default:
                os << ch;
                break;
        }
    }
    os << '"';
}

void print_number_array(std::ostream& os, const std::vector<double>& values) {
    os << '[';
    for (std::size_t i = 0; i < values.size(); ++i) {
        if (i > 0) {
            os << ',';
        }
        os << std::fixed << std::setprecision(2) << values[i];
    }
    os << ']';
}

void print_aggregate(std::ostream& os, const Aggregate& value) {
    os << "{\"median\":" << std::fixed << std::setprecision(2) << value.median
       << ",\"min\":" << value.min
       << ",\"max\":" << value.max
       << ",\"mean\":" << value.mean
       << ",\"stddev\":" << value.stddev
       << ",\"cv\":" << std::setprecision(6) << value.cv
       << '}';
}

void print_metric_result(std::ostream& os, std::string_view unit, const std::vector<double>& samples) {
    os << "{\"unit\":";
    print_json_string(os, unit);
    os << ",\"samples\":";
    print_number_array(os, samples);
    os << ",\"aggregate\":";
    print_aggregate(os, aggregate(samples));
    os << '}';
}

void print_json_result(std::ostream& os, const Options& options, const BenchResult& result) {
    os << std::fixed << std::setprecision(2)
       << "{\"type\":\"result\""
       << ",\"worker_version\":";
    print_json_string(os, kWorkerVersion);
    os << ",\"protocol_version\":" << kProtocolVersion
       << ",\"elapsed_ms\":" << result.elapsed_ms
       << ",\"options\":{\"size_mib\":" << options.size_mib
       << ",\"iterations\":" << options.iterations
       << ",\"latency_steps\":" << options.latency_steps
       << ",\"threads\":1"
       << ",\"working_set_kind\":\"memory\"}"
       << ",\"kernel\":{\"read\":\"scalar_u64_unrolled\",\"write\":\"crt_memset\",\"copy\":\"crt_memcpy\",\"latency\":\"pointer_chase\"}"
       << ",\"metrics\":{\"read\":";
    print_metric_result(os, "mib_s", result.read_samples);
    os << ",\"write\":";
    print_metric_result(os, "mib_s", result.write_samples);
    os << ",\"copy\":";
    print_metric_result(os, "mib_s", result.copy_samples);
    os << ",\"latency\":";
    print_metric_result(os, "ns", result.latency_samples);
    os << "}}\n";
}

BenchResult run_bench(const Options& options) {
    const auto benchmark_start = std::chrono::steady_clock::now();
    const std::size_t bytes = static_cast<std::size_t>(options.size_mib) * kMiB;
    auto src = make_aligned_buffer(bytes);
    auto dst = make_aligned_buffer(bytes);

    touch_pages(src.get(), bytes);
    touch_pages(dst.get(), bytes);

    for (std::size_t i = 0; i < bytes; ++i) {
        src.get()[i] = static_cast<std::uint8_t>(i * 131u + 17u);
    }

    (void)run_read(src.get(), bytes, 1);
    (void)run_write(dst.get(), bytes, 1);
    (void)run_copy(dst.get(), src.get(), bytes, 1);

    BenchResult result;
    result.read_samples = run_read(src.get(), bytes, options.iterations);
    result.read_mib_s = aggregate(result.read_samples).median;
    if (options.progress_json) {
        emit_progress_metric("read", result.read_mib_s, "mib_s");
    }

    result.write_samples = run_write(dst.get(), bytes, options.iterations);
    result.write_mib_s = aggregate(result.write_samples).median;
    if (options.progress_json) {
        emit_progress_metric("write", result.write_mib_s, "mib_s");
    }

    result.copy_samples = run_copy(dst.get(), src.get(), bytes, options.iterations);
    result.copy_mib_s = aggregate(result.copy_samples).median;
    if (options.progress_json) {
        emit_progress_metric("copy", result.copy_mib_s, "mib_s");
    }

    src.reset();
    dst.reset();

    result.latency_samples = run_latency(bytes, options.latency_steps, options.iterations);
    result.latency_ns = aggregate(result.latency_samples).median;
    if (options.progress_json) {
        emit_progress_metric("latency", result.latency_ns, "ns");
    }

    const auto benchmark_finish = std::chrono::steady_clock::now();
    result.elapsed_ms = std::chrono::duration<double, std::milli>(benchmark_finish - benchmark_start).count();
    return result;
}

void print_result(const Options& options, const BenchResult& result) {
    if (options.csv) {
        std::cout << "size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns\n"
                  << options.size_mib << ','
                  << std::fixed << std::setprecision(2)
                  << result.read_mib_s << ','
                  << result.write_mib_s << ','
                  << result.copy_mib_s << ','
                  << result.latency_ns << '\n';
        return;
    }

    if (options.json) {
        print_json_result(std::cout, options, result);
        return;
    }

    std::cout << "Memory Benchmark\n"
              << "----------------\n"
              << "Buffer size : " << options.size_mib << " MiB\n"
              << "Iterations  : " << options.iterations << " median samples\n"
              << "Latency ops : " << options.latency_steps << "\n\n"
              << std::fixed << std::setprecision(2)
              << "Read        : " << std::setw(12) << result.read_mib_s << " MiB/s\n"
              << "Write       : " << std::setw(12) << result.write_mib_s << " MiB/s\n"
              << "Copy        : " << std::setw(12) << result.copy_mib_s << " MiB/s\n"
              << "Latency     : " << std::setw(12) << result.latency_ns << " ns\n";
}

} // namespace

int main(int argc, char** argv) {
    try {
        const Options options = parse_args(argc, argv);
        pin_to_current_cpu();
        if (options.progress_json) {
            emit_progress_started(options);
        }

        const BenchResult result = run_bench(options);
        if (options.progress_json) {
            print_json_result(std::cout, options, result);
            emit_progress_completed();
            return 0;
        }

        print_result(options, result);
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "error: " << ex.what() << "\n\n";
        print_usage();
        return 1;
    }
}
