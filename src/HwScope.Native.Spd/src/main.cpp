#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr int kSchemaVersion = 1;
constexpr std::string_view kWorkerVersion = "0.1.0";

struct Options {
    bool json = false;
    bool help = false;
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
        << "Usage: spd --json\n"
        << "\n"
        << "HwScope SPD worker protocol scaffold. This build exposes the JSON contract\n"
        << "and reports a non-fatal platformBlocked status until raw SMBus/SPD access\n"
        << "is implemented for the current platform.\n";
}

Options parse_args(int argc, char** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        const std::string_view arg(argv[i]);
        if (arg == "--json") {
            options.json = true;
        } else if (arg == "--help" || arg == "-h") {
            options.help = true;
        } else {
            throw std::invalid_argument("Unknown argument: " + std::string(arg));
        }
    }

    return options;
}

void print_scaffold_json(std::ostream& os) {
    os << "{\"schemaVersion\":" << kSchemaVersion
       << ",\"workerVersion\":";
    print_json_string(os, kWorkerVersion);
    os << ",\"status\":\"platformBlocked\""
       << ",\"modules\":[]"
       << ",\"diagnostics\":[";
    print_json_string(os, "Native SPD worker scaffold is available, but raw SMBus/SPD reading is not implemented yet.");
    os << ',';
    print_json_string(os, "HwScope will continue to show WMI/SMBIOS-backed memory fields and SPD placeholders.");
    os << "]}\n";
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

        print_scaffold_json(std::cout);
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "error: " << ex.what() << "\n\n";
        print_usage();
        return 1;
    }
}
