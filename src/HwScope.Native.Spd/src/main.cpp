#include <iostream>
#include <exception>
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr int kSchemaVersion = 1;
constexpr std::string_view kWorkerVersion = "0.1.0";

struct Options {
    bool json = false;
    bool help = false;
    std::string backend = "auto";
    std::string fixture_path;
};

void print_json_string(std::ostream& os, std::string_view value) {
    os << '"';
    for (const char ch : value) {
        switch (ch) {
        case '"':
            os << "\\\"";
            break;
        case '\\':
            os << "\\\\";
            break;
        case '\b':
            os << "\\b";
            break;
        case '\f':
            os << "\\f";
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

void print_usage() {
    std::cout
        << "Usage: spd --json [--backend auto|fixture] [--fixture PATH]\n"
        << "\n"
        << "HwScope SPD worker protocol scaffold. This build exposes the JSON contract\n"
        << "and reports a non-fatal notImplemented status until raw SMBus/SPD access\n"
        << "is implemented for the current platform. The fixture backend can emit\n"
        << "structured SPD module data from a development JSON fixture.\n";
}

Options parse_args(int argc, char** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        const std::string_view arg(argv[i]);
        if (arg == "--json") {
            options.json = true;
        } else if (arg == "--backend") {
            if (i + 1 >= argc) {
                throw std::invalid_argument("Missing value for --backend");
            }

            options.backend = argv[++i];
        } else if (arg == "--fixture") {
            if (i + 1 >= argc) {
                throw std::invalid_argument("Missing value for --fixture");
            }

            options.fixture_path = argv[++i];
        } else if (arg == "--help" || arg == "-h") {
            options.help = true;
        } else {
            throw std::invalid_argument("Unknown argument: " + std::string(arg));
        }
    }

    return options;
}

std::string read_all_text(const std::string& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw std::runtime_error("Unable to open fixture: " + path);
    }

    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

void print_scaffold_json(std::ostream& os) {
    os << "{\"schemaVersion\":" << kSchemaVersion
       << ",\"workerVersion\":";
    print_json_string(os, kWorkerVersion);
    os << ",\"status\":\"platformBlocked\""
       << ",\"modules\":[]"
       << ",\"diagnostics\":[";
    print_json_string(os, "Windows does not expose a stable user-mode API for raw SPD EEPROM reads on this platform.");
    os << ',';
    print_json_string(os, "A privileged SMBus/SPD backend or supported controller-specific reader is required for raw hardware access.");
    os << ',';
    print_json_string(os, "Set HWSCOPE_SPD_FIXTURE to a fixture JSON path to validate parser/UI integration.");
    os << "]}\n";
}

void print_fixture_json(std::ostream& os, const std::string& fixture_path) {
    if (fixture_path.empty()) {
        throw std::invalid_argument("--backend fixture requires --fixture PATH");
    }

    // Fixture files are already worker-payload-shaped JSON. Keeping this pass-through
    // lets Core/UI integration progress before unsafe raw SMBus access exists.
    os << read_all_text(fixture_path);
    os << '\n';
}

} // namespace

int main(int argc, char** argv) {
    try {
        const Options options = parse_args(argc, argv);
        if (options.help) {
            print_usage();
            return 0;
        }

        if (!options.json) {
            print_usage();
            return 1;
        }

        if (options.backend == "fixture") {
            print_fixture_json(std::cout, options.fixture_path);
        } else if (options.backend == "auto") {
            print_scaffold_json(std::cout);
        } else {
            throw std::invalid_argument("Unsupported backend: " + options.backend);
        }

        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "error: " << ex.what() << "\n\n";
        print_usage();
        return 1;
    }
}
