#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
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

volatile std::uint64_t g_sink = 0;

struct Options {
    std::uint64_t size_mib = kDefaultSizeMiB;
    int iterations = kDefaultIterations;
    std::uint64_t latency_steps = kDefaultLatencySteps;
    bool csv = false;
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
};

void print_usage() {
    std::cout
        << "Usage: membench [--size-mib N] [--iterations N] [--latency-steps N] [--csv]\n"
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

void touch_pages(std::uint8_t* data, std::size_t bytes) {
    constexpr std::size_t page = 4096;
    for (std::size_t i = 0; i < bytes; i += page) {
        data[i] = static_cast<std::uint8_t>(i);
    }
}

double run_read(std::uint8_t* data, std::size_t bytes, int iterations) {
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

    return median(std::move(samples));
}

double run_write(std::uint8_t* data, std::size_t bytes, int iterations) {
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

    return median(std::move(samples));
}

double run_copy(std::uint8_t* dst, const std::uint8_t* src, std::size_t bytes, int iterations) {
    std::vector<double> samples;
    samples.reserve(static_cast<std::size_t>(iterations));

    for (int round = 0; round < iterations; ++round) {
        const double seconds = elapsed_seconds([&] {
            std::memcpy(dst, src, bytes);
        });
        g_sink ^= dst[(static_cast<std::size_t>(round) * 4099) % bytes];
        samples.push_back(static_cast<double>(bytes) / seconds / static_cast<double>(kMiB));
    }

    return median(std::move(samples));
}

double run_latency(std::size_t bytes, std::uint64_t steps) {
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

    for (std::uint64_t i = 0; i < std::min<std::uint64_t>(steps / 10, 1'000'000); ++i) {
        index = chain[index];
    }

    const double seconds = elapsed_seconds([&] {
        for (std::uint64_t i = 0; i < steps; ++i) {
            index = chain[index];
        }
    });

    g_sink ^= index;
    return seconds * 1'000'000'000.0 / static_cast<double>(steps);
}

BenchResult run_bench(const Options& options) {
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
    result.read_mib_s = run_read(src.get(), bytes, options.iterations);
    result.write_mib_s = run_write(dst.get(), bytes, options.iterations);
    result.copy_mib_s = run_copy(dst.get(), src.get(), bytes, options.iterations);

    src.reset();
    dst.reset();

    result.latency_ns = run_latency(bytes, options.latency_steps);
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
        const BenchResult result = run_bench(options);
        print_result(options, result);
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "error: " << ex.what() << "\n\n";
        print_usage();
        return 1;
    }
}
