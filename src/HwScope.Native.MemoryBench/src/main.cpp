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
constexpr int kDefaultWarmupRuns = 1;
constexpr int kDefaultMaxSamples = 11;
constexpr double kDefaultTargetSampleMs = 120.0;
constexpr double kDefaultMaxCv = 0.03;
constexpr std::string_view kWorkerVersion = "0.3.0";
constexpr int kProtocolVersion = 3;

volatile std::uint64_t g_sink = 0;

struct Options {
    std::uint64_t size_mib = kDefaultSizeMiB;
    int iterations = kDefaultIterations;
    std::uint64_t latency_steps = kDefaultLatencySteps;
    int warmup_runs = kDefaultWarmupRuns;
    int min_samples = kDefaultIterations;
    int max_samples = kDefaultMaxSamples;
    double target_sample_ms = kDefaultTargetSampleMs;
    double max_cv = kDefaultMaxCv;
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
    std::vector<std::uint64_t> read_inner_iterations;
    std::vector<std::uint64_t> write_inner_iterations;
    std::vector<std::uint64_t> copy_inner_iterations;
    std::vector<std::uint64_t> latency_inner_iterations;
    double elapsed_ms = 0.0;
    std::uint64_t timer_frequency_hz = 0;
    bool read_converged = false;
    bool write_converged = false;
    bool copy_converged = false;
    bool latency_converged = false;
};

struct Aggregate {
    double median = 0.0;
    double min = 0.0;
    double max = 0.0;
    double mean = 0.0;
    double stddev = 0.0;
    double cv = 0.0;
};

struct TimedSample {
    double value = 0.0;
    double elapsed_ms = 0.0;
};

struct SampleSeries {
    std::vector<double> values;
    std::vector<std::uint64_t> inner_iterations;
    bool converged = false;
};

void print_usage() {
    std::cout
        << "Usage: membench [--size-mib N] [--iterations N] [--latency-steps N]\n"
        << "                [--warmup-runs N] [--min-samples N] [--max-samples N]\n"
        << "                [--target-sample-ms N] [--max-cv N]\n"
        << "                [--csv] [--json] [--progress-json]\n"
        << "\n"
        << "Defaults:\n"
        << "  --size-mib " << kDefaultSizeMiB << "\n"
        << "  --iterations " << kDefaultIterations << " (legacy alias for --min-samples)\n"
        << "  --latency-steps " << kDefaultLatencySteps << "\n"
        << "  --warmup-runs " << kDefaultWarmupRuns << "\n"
        << "  --min-samples " << kDefaultIterations << "\n"
        << "  --max-samples " << kDefaultMaxSamples << "\n"
        << "  --target-sample-ms " << kDefaultTargetSampleMs << "\n"
        << "  --max-cv " << kDefaultMaxCv << "\n";
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

double parse_double(std::string_view value, std::string_view name) {
    std::size_t consumed = 0;
    double parsed = 0.0;
    try {
        parsed = std::stod(std::string(value), &consumed);
    } catch (const std::exception&) {
        throw std::invalid_argument("Invalid number for " + std::string(name));
    }

    if (consumed != value.size()) {
        throw std::invalid_argument("Invalid number for " + std::string(name));
    }
    if (!std::isfinite(parsed)) {
        throw std::invalid_argument("Invalid finite number for " + std::string(name));
    }
    return parsed;
}

std::uint64_t checked_multiply_u64(std::uint64_t left, std::uint64_t right, std::string_view name) {
    if (right != 0 && left > std::numeric_limits<std::uint64_t>::max() / right) {
        throw std::overflow_error(std::string(name) + " overflow");
    }

    return left * right;
}

Options parse_args(int argc, char** argv) {
    Options options;
    bool max_samples_specified = false;

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
            options.min_samples = static_cast<int>(value);
        } else if (arg == "--latency-steps") {
            options.latency_steps = parse_u64(require_value(arg), arg);
        } else if (arg == "--warmup-runs") {
            const auto value = parse_u64(require_value(arg), arg);
            if (value > 100) {
                throw std::invalid_argument("--warmup-runs must be in [0, 100]");
            }
            options.warmup_runs = static_cast<int>(value);
        } else if (arg == "--min-samples") {
            const auto value = parse_u64(require_value(arg), arg);
            if (value == 0 || value > 1000) {
                throw std::invalid_argument("--min-samples must be in [1, 1000]");
            }
            options.min_samples = static_cast<int>(value);
            options.iterations = static_cast<int>(value);
        } else if (arg == "--max-samples") {
            const auto value = parse_u64(require_value(arg), arg);
            if (value == 0 || value > 1000) {
                throw std::invalid_argument("--max-samples must be in [1, 1000]");
            }
            options.max_samples = static_cast<int>(value);
            max_samples_specified = true;
        } else if (arg == "--target-sample-ms") {
            options.target_sample_ms = parse_double(require_value(arg), arg);
        } else if (arg == "--max-cv") {
            options.max_cv = parse_double(require_value(arg), arg);
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
    if (!max_samples_specified && options.max_samples < options.min_samples) {
        options.max_samples = options.min_samples;
    }
    if (options.max_samples < options.min_samples) {
        throw std::invalid_argument("--max-samples must be greater than or equal to --min-samples");
    }
    if (options.target_sample_ms <= 0.0 || options.target_sample_ms > 60'000.0) {
        throw std::invalid_argument("--target-sample-ms must be in (0, 60000]");
    }
    if (options.max_cv < 0.0 || options.max_cv > 1.0) {
        throw std::invalid_argument("--max-cv must be in [0, 1]");
    }
    if (options.size_mib > std::numeric_limits<std::size_t>::max() / kMiB) {
        throw std::invalid_argument("--size-mib is too large for this process");
    }
    if ((options.csv ? 1 : 0) + (options.json ? 1 : 0) + (options.progress_json ? 1 : 0) > 1) {
        throw std::invalid_argument("--csv, --json, and --progress-json are mutually exclusive");
    }

    return options;
}

class Timer {
public:
    Timer() {
#if defined(_WIN32)
        LARGE_INTEGER value{};
        if (!QueryPerformanceFrequency(&value) || value.QuadPart <= 0) {
            throw std::runtime_error("QueryPerformanceFrequency failed");
        }

        frequency_ = value.QuadPart;
#else
        frequency_ = 1'000'000'000;
#endif
    }

    std::uint64_t frequency_hz() const noexcept {
        return frequency_;
    }

    std::uint64_t now_ticks() const {
#if defined(_WIN32)
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return static_cast<std::uint64_t>(value.QuadPart);
#else
        const auto now = std::chrono::steady_clock::now().time_since_epoch();
        return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(now).count());
#endif
    }

    double elapsed_seconds(std::uint64_t start, std::uint64_t finish) const {
        return static_cast<double>(finish - start) / static_cast<double>(frequency_);
    }

private:
    std::uint64_t frequency_ = 0;
};

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

bool has_converged(const std::vector<double>& values, double max_cv) {
    return values.size() >= 2 && aggregate(values).cv <= max_cv;
}

template <typename Measure, typename Convert>
TimedSample time_repeated(const Timer& timer, Measure&& measure, Convert&& convert, std::uint64_t inner_iterations) {
    const auto start = timer.now_ticks();
    const double work = measure(inner_iterations);
    const auto finish = timer.now_ticks();
    const auto elapsed_seconds = timer.elapsed_seconds(start, finish);
    return TimedSample{
        convert(work, elapsed_seconds),
        elapsed_seconds * 1000.0
    };
}

template <typename Measure, typename Convert>
SampleSeries collect_adaptive_samples(const Options& options, const Timer& timer, Measure&& measure, Convert&& convert) {
    for (int warmup = 0; warmup < options.warmup_runs; ++warmup) {
        (void)time_repeated(timer, measure, convert, 1);
    }

    SampleSeries series;
    series.values.reserve(static_cast<std::size_t>(options.max_samples));
    series.inner_iterations.reserve(static_cast<std::size_t>(options.max_samples));

    std::uint64_t inner_iterations = 1;
    while (static_cast<int>(series.values.size()) < options.max_samples) {
        TimedSample sample;
        for (;;) {
            sample = time_repeated(timer, measure, convert, inner_iterations);
            if (sample.elapsed_ms >= options.target_sample_ms || inner_iterations >= (std::numeric_limits<std::uint64_t>::max() / 2)) {
                break;
            }

            const auto scale = std::max<std::uint64_t>(
                2,
                static_cast<std::uint64_t>(std::ceil(options.target_sample_ms / std::max(sample.elapsed_ms, 0.001))));
            const auto max_multiplier = (std::numeric_limits<std::uint64_t>::max() / inner_iterations);
            inner_iterations *= std::min(scale, max_multiplier);
        }

        series.values.push_back(sample.value);
        series.inner_iterations.push_back(inner_iterations);

        if (static_cast<int>(series.values.size()) >= options.min_samples && has_converged(series.values, options.max_cv)) {
            series.converged = true;
            break;
        }
    }

    return series;
}

void touch_pages(std::uint8_t* data, std::size_t bytes) {
    constexpr std::size_t page = 4096;
    for (std::size_t i = 0; i < bytes; i += page) {
        data[i] = static_cast<std::uint8_t>(i);
    }
}

std::uint64_t run_read_once(std::uint8_t* data, std::size_t bytes) {
    constexpr std::size_t unroll = 8;
    constexpr std::size_t stride = sizeof(std::uint64_t) * unroll;
    const auto count = bytes / sizeof(std::uint64_t);
    const auto* words = reinterpret_cast<const std::uint64_t*>(data);
    std::uint64_t sum0 = 0;
    std::uint64_t sum1 = 0;
    std::uint64_t sum2 = 0;
    std::uint64_t sum3 = 0;
    std::uint64_t sum4 = 0;
    std::uint64_t sum5 = 0;
    std::uint64_t sum6 = 0;
    std::uint64_t sum7 = 0;

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

    g_sink ^= sum0 ^ sum1 ^ sum2 ^ sum3 ^ sum4 ^ sum5 ^ sum6 ^ sum7 ^ stride;
    return static_cast<std::uint64_t>(bytes);
}

std::uint64_t run_write_once(std::uint8_t* data, std::size_t bytes, std::uint64_t round) {
    const auto value = static_cast<int>((round * 37) & 0xff);
    std::memset(data, value, bytes);
    g_sink ^= data[static_cast<std::size_t>(round) % bytes];
    return static_cast<std::uint64_t>(bytes);
}

std::uint64_t run_copy_once(std::uint8_t* dst, const std::uint8_t* src, std::size_t bytes, std::uint64_t round) {
    std::memcpy(dst, src, bytes);
    g_sink ^= dst[(static_cast<std::size_t>(round) * 4099) % bytes];
    return static_cast<std::uint64_t>(bytes);
}

SampleSeries run_read(std::uint8_t* data, std::size_t bytes, const Options& options, const Timer& timer) {
    return collect_adaptive_samples(options, timer, [&](std::uint64_t inner_iterations) {
        const auto total_bytes = checked_multiply_u64(static_cast<std::uint64_t>(bytes), inner_iterations, "read bytes");
        for (std::uint64_t round = 0; round < inner_iterations; ++round) {
            (void)run_read_once(data, bytes);
        }

        return static_cast<double>(total_bytes) / static_cast<double>(kMiB);
    }, [](double mib, double seconds) {
        return mib / seconds;
    });
}

SampleSeries run_write(std::uint8_t* data, std::size_t bytes, const Options& options, const Timer& timer) {
    return collect_adaptive_samples(options, timer, [&](std::uint64_t inner_iterations) {
        const auto total_bytes = checked_multiply_u64(static_cast<std::uint64_t>(bytes), inner_iterations, "write bytes");
        for (std::uint64_t round = 0; round < inner_iterations; ++round) {
            (void)run_write_once(data, bytes, round);
        }

        return static_cast<double>(total_bytes) / static_cast<double>(kMiB);
    }, [](double mib, double seconds) {
        return mib / seconds;
    });
}

SampleSeries run_copy(std::uint8_t* dst, const std::uint8_t* src, std::size_t bytes, const Options& options, const Timer& timer) {
    return collect_adaptive_samples(options, timer, [&](std::uint64_t inner_iterations) {
        const auto total_bytes = checked_multiply_u64(static_cast<std::uint64_t>(bytes), inner_iterations, "copy bytes");
        for (std::uint64_t round = 0; round < inner_iterations; ++round) {
            (void)run_copy_once(dst, src, bytes, round);
        }

        return static_cast<double>(total_bytes) / static_cast<double>(kMiB);
    }, [](double mib, double seconds) {
        return mib / seconds;
    });
}

SampleSeries run_latency(std::size_t bytes, std::uint64_t steps, const Options& options, const Timer& timer) {
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
    const std::uint64_t base_steps = std::max<std::uint64_t>(1, steps / static_cast<std::uint64_t>(options.min_samples));

    for (std::uint64_t i = 0; i < std::min<std::uint64_t>(steps / 10, 1'000'000); ++i) {
        index = chain[index];
    }

    auto series = collect_adaptive_samples(options, timer, [&](std::uint64_t inner_iterations) {
        const auto total_steps = checked_multiply_u64(base_steps, inner_iterations, "latency steps");
        for (std::uint64_t i = 0; i < total_steps; ++i) {
            index = chain[index];
        }

        return static_cast<double>(total_steps);
    }, [](double operations, double seconds) {
        return seconds * 1'000'000'000.0 / operations;
    });
    g_sink ^= index;
    return series;
}

void emit_progress_started(const Options& options) {
    std::cout << "{\"type\":\"started\",\"size_mib\":" << options.size_mib
              << ",\"iterations\":" << options.iterations
              << ",\"latency_steps\":" << options.latency_steps
              << ",\"warmup_runs\":" << options.warmup_runs
              << ",\"min_samples\":" << options.min_samples
              << ",\"max_samples\":" << options.max_samples
              << ",\"target_sample_ms\":" << options.target_sample_ms
              << ",\"max_cv\":" << options.max_cv << "}\n"
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

void print_u64_array(std::ostream& os, const std::vector<std::uint64_t>& values) {
    os << '[';
    for (std::size_t i = 0; i < values.size(); ++i) {
        if (i > 0) {
            os << ',';
        }
        os << values[i];
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

void print_metric_result(
    std::ostream& os,
    std::string_view unit,
    const std::vector<double>& samples,
    const std::vector<std::uint64_t>& inner_iterations,
    bool converged) {
    os << "{\"unit\":";
    print_json_string(os, unit);
    os << ",\"samples\":";
    print_number_array(os, samples);
    os << ",\"inner_iterations\":";
    print_u64_array(os, inner_iterations);
    os << ",\"converged\":" << (converged ? "true" : "false");
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
       << ",\"timer\":{\"name\":\"";
#if defined(_WIN32)
    os << "QueryPerformanceCounter";
#else
    os << "steady_clock";
#endif
    os << "\",\"frequency_hz\":" << result.timer_frequency_hz << "}"
       << ",\"options\":{\"size_mib\":" << options.size_mib
       << ",\"iterations\":" << options.iterations
       << ",\"latency_steps\":" << options.latency_steps
       << ",\"warmup_runs\":" << options.warmup_runs
       << ",\"min_samples\":" << options.min_samples
       << ",\"max_samples\":" << options.max_samples
       << ",\"target_sample_ms\":" << options.target_sample_ms
       << ",\"max_cv\":" << std::setprecision(6) << options.max_cv << std::setprecision(2)
       << ",\"threads\":1"
       << ",\"working_set_kind\":\"memory\"}"
       << ",\"kernel\":{\"read\":\"scalar_u64_unrolled\",\"write\":\"crt_memset\",\"copy\":\"crt_memcpy\",\"latency\":\"pointer_chase\"}"
       << ",\"metrics\":{\"read\":";
    print_metric_result(os, "mib_s", result.read_samples, result.read_inner_iterations, result.read_converged);
    os << ",\"write\":";
    print_metric_result(os, "mib_s", result.write_samples, result.write_inner_iterations, result.write_converged);
    os << ",\"copy\":";
    print_metric_result(os, "mib_s", result.copy_samples, result.copy_inner_iterations, result.copy_converged);
    os << ",\"latency\":";
    print_metric_result(os, "ns", result.latency_samples, result.latency_inner_iterations, result.latency_converged);
    os << "}}\n";
}

BenchResult run_bench(const Options& options) {
    const Timer timer;
    const auto benchmark_start = timer.now_ticks();
    const std::size_t bytes = static_cast<std::size_t>(options.size_mib) * kMiB;
    auto src = make_aligned_buffer(bytes);
    auto dst = make_aligned_buffer(bytes);

    touch_pages(src.get(), bytes);
    touch_pages(dst.get(), bytes);

    for (std::size_t i = 0; i < bytes; ++i) {
        src.get()[i] = static_cast<std::uint8_t>(i * 131u + 17u);
    }

    BenchResult result;
    result.timer_frequency_hz = timer.frequency_hz();

    const auto read_series = run_read(src.get(), bytes, options, timer);
    result.read_samples = read_series.values;
    result.read_inner_iterations = read_series.inner_iterations;
    result.read_converged = read_series.converged;
    result.read_mib_s = aggregate(result.read_samples).median;
    if (options.progress_json) {
        emit_progress_metric("read", result.read_mib_s, "mib_s");
    }

    const auto write_series = run_write(dst.get(), bytes, options, timer);
    result.write_samples = write_series.values;
    result.write_inner_iterations = write_series.inner_iterations;
    result.write_converged = write_series.converged;
    result.write_mib_s = aggregate(result.write_samples).median;
    if (options.progress_json) {
        emit_progress_metric("write", result.write_mib_s, "mib_s");
    }

    const auto copy_series = run_copy(dst.get(), src.get(), bytes, options, timer);
    result.copy_samples = copy_series.values;
    result.copy_inner_iterations = copy_series.inner_iterations;
    result.copy_converged = copy_series.converged;
    result.copy_mib_s = aggregate(result.copy_samples).median;
    if (options.progress_json) {
        emit_progress_metric("copy", result.copy_mib_s, "mib_s");
    }

    src.reset();
    dst.reset();

    result.latency_samples = {};
    const auto latency_series = run_latency(bytes, options.latency_steps, options, timer);
    result.latency_samples = latency_series.values;
    result.latency_inner_iterations = latency_series.inner_iterations;
    result.latency_converged = latency_series.converged;
    result.latency_ns = aggregate(result.latency_samples).median;
    if (options.progress_json) {
        emit_progress_metric("latency", result.latency_ns, "ns");
    }

    const auto benchmark_finish = timer.now_ticks();
    result.elapsed_ms = timer.elapsed_seconds(benchmark_start, benchmark_finish) * 1000.0;
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
              << "Samples     : min " << options.min_samples << ", max " << options.max_samples << "\n"
              << "Warmup runs : " << options.warmup_runs << "\n"
              << "Target time : " << options.target_sample_ms << " ms/sample\n"
              << "Timer       : "
#if defined(_WIN32)
              << "QueryPerformanceCounter"
#else
              << "steady_clock"
#endif
              << " (" << result.timer_frequency_hz << " Hz)\n"
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
