#include <iostream>
#include <algorithm>
#include <cctype>
#include <exception>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>
#endif

namespace {

constexpr int kSchemaVersion = 1;
constexpr std::string_view kWorkerVersion = "0.2.0";

struct Options {
    bool json = false;
    bool help = false;
    std::string backend = "auto";
    std::string fixture_path;
};

struct SpdOrganization {
    int rank_count = 0;
    int bank_group_count = 0;
    int banks_per_group = 0;
    int device_width_bits = 0;
    int bus_width_bits = 0;
    int data_width_bits = 0;
    int total_width_bits = 0;
};

struct SpdVoltages {
    int vdd_mv = 0;
    int vddq_mv = 0;
    int vpp_mv = 0;
};

struct TimingProfile {
    std::string name;
    std::string kind;
    double frequency_mhz = 0.0;
    int effective_rate_mtps = 0;
    int cas_latency = 0;
    int trcd = 0;
    int trp = 0;
    int tras = 0;
    int trc = 0;
    int voltage_mv = 0;
};

struct ParsedSpdModule {
    std::string locator;
    std::string memory_type;
    std::string module_type;
    unsigned long long capacity_bytes = 0;
    std::string manufacturer;
    std::string dram_manufacturer;
    std::string part_number;
    std::string serial_number;
    int manufacturing_week = 0;
    int manufacturing_year = 0;
    std::string revision;
    SpdOrganization organization;
    SpdVoltages voltages;
    int raw_byte_count = 0;
    bool checksum_ok = false;
    bool crc_ok = false;
    std::string raw_sha256;
    std::vector<TimingProfile> timing_profiles;
    std::vector<std::pair<std::string, std::string>> features;
    std::vector<std::string> diagnostics;
};

struct ParseResult {
    std::string status;
    std::vector<ParsedSpdModule> modules;
    std::vector<std::string> diagnostics;
};

void print_json_string(std::ostream& os, std::string_view value) {
    os << '"';
    for (const unsigned char raw : value) {
        const char ch = static_cast<char>(raw);
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
            if (raw < 0x20) {
                os << "\\u00";
                const char* digits = "0123456789abcdef";
                os << digits[(raw >> 4) & 0x0f] << digits[raw & 0x0f];
            } else {
                os << ch;
            }
            break;
        }
    }
    os << '"';
}

void print_usage() {
    std::cout
        << "Usage: spd --json [--backend auto|fixture] [--fixture PATH]\n"
        << "\n"
        << "HwScope SPD parser worker. Hardware SPD acquisition is not implemented.\n"
        << "The fixture backend parses offline SPD bytes or emits development payloads.\n";
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

std::string trim_ascii(std::string value) {
    auto is_space = [](unsigned char ch) { return std::isspace(ch) != 0; };
    value.erase(value.begin(), std::find_if(value.begin(), value.end(), [&](char ch) {
        return !is_space(static_cast<unsigned char>(ch));
    }));
    value.erase(std::find_if(value.rbegin(), value.rend(), [&](char ch) {
        return !is_space(static_cast<unsigned char>(ch));
    }).base(), value.end());
    return value;
}

int hex_value(char ch) {
    if (ch >= '0' && ch <= '9') {
        return ch - '0';
    }

    if (ch >= 'a' && ch <= 'f') {
        return ch - 'a' + 10;
    }

    if (ch >= 'A' && ch <= 'F') {
        return ch - 'A' + 10;
    }

    return -1;
}

std::vector<unsigned char> parse_hex_bytes(std::string_view hex) {
    std::vector<unsigned char> bytes;
    int high = -1;
    for (size_t i = 0; i < hex.size(); ++i) {
        const char ch = hex[i];
        if (ch == '\\' && i + 1 < hex.size()) {
            const char next = hex[i + 1];
            if (next == 'n' || next == 'r' || next == 't') {
                ++i;
                continue;
            }
        }

        if (std::isspace(static_cast<unsigned char>(ch)) || ch == '_' || ch == '-') {
            continue;
        }

        const int value = hex_value(ch);
        if (value < 0) {
            throw std::invalid_argument("bytesHex contains a non-hex character.");
        }

        if (high < 0) {
            high = value;
        } else {
            bytes.push_back(static_cast<unsigned char>((high << 4) | value));
            high = -1;
        }
    }

    if (high >= 0) {
        throw std::invalid_argument("bytesHex contains an odd number of hex digits.");
    }

    return bytes;
}

std::string extract_json_string(const std::string& json, std::string_view property_name) {
    const std::string quoted_name = "\"" + std::string(property_name) + "\"";
    const size_t name_offset = json.find(quoted_name);
    if (name_offset == std::string::npos) {
        return {};
    }

    const size_t colon_offset = json.find(':', name_offset + quoted_name.size());
    if (colon_offset == std::string::npos) {
        return {};
    }

    size_t quote_offset = colon_offset + 1;
    while (quote_offset < json.size() && std::isspace(static_cast<unsigned char>(json[quote_offset]))) {
        ++quote_offset;
    }

    if (quote_offset >= json.size() || json[quote_offset] != '"') {
        return {};
    }

    std::string value;
    bool escaped = false;
    for (size_t i = quote_offset + 1; i < json.size(); ++i) {
        const char ch = json[i];
        if (escaped) {
            switch (ch) {
            case '"':
            case '\\':
            case '/':
                value.push_back(ch);
                break;
            case 'n':
                value.push_back('\n');
                break;
            case 'r':
                value.push_back('\r');
                break;
            case 't':
                value.push_back('\t');
                break;
            default:
                value.push_back(ch);
                break;
            }
            escaped = false;
        } else if (ch == '\\') {
            escaped = true;
        } else if (ch == '"') {
            return value;
        } else {
            value.push_back(ch);
        }
    }

    return {};
}

std::string read_spd_ascii(const std::vector<unsigned char>& bytes, size_t offset, size_t length) {
    if (offset >= bytes.size()) {
        return {};
    }

    const size_t end = (std::min)(bytes.size(), offset + length);
    std::string value;
    for (size_t i = offset; i < end; ++i) {
        const unsigned char ch = bytes[i];
        value.push_back(ch >= 0x20 && ch <= 0x7e ? static_cast<char>(ch) : ' ');
    }

    return trim_ascii(value);
}

std::string hex_byte(unsigned char value) {
    std::ostringstream stream;
    stream << std::uppercase << std::hex << std::setw(2) << std::setfill('0') << static_cast<int>(value);
    return stream.str();
}

std::string manufacturer_id_hex(const std::vector<unsigned char>& bytes, size_t offset) {
    if (offset + 1 >= bytes.size()) {
        return {};
    }

    return "ID " + hex_byte(bytes[offset]) + hex_byte(bytes[offset + 1]);
}

std::string serial_hex(const std::vector<unsigned char>& bytes, size_t offset) {
    if (offset + 3 >= bytes.size()) {
        return {};
    }

    return hex_byte(bytes[offset]) + hex_byte(bytes[offset + 1]) + hex_byte(bytes[offset + 2]) + hex_byte(bytes[offset + 3]);
}

#ifdef _WIN32
std::string sha256_hex(const std::vector<unsigned char>& bytes) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    if (status < 0) {
        return {};
    }

    DWORD hash_length = 0;
    DWORD result_size = 0;
    status = BCryptGetProperty(
        algorithm,
        BCRYPT_HASH_LENGTH,
        reinterpret_cast<PUCHAR>(&hash_length),
        sizeof(hash_length),
        &result_size,
        0);
    if (status < 0 || hash_length == 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return {};
    }

    status = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0);
    if (status < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return {};
    }

    status = BCryptHashData(
        hash,
        const_cast<PUCHAR>(reinterpret_cast<const unsigned char*>(bytes.data())),
        static_cast<ULONG>(bytes.size()),
        0);
    std::vector<unsigned char> digest(hash_length);
    if (status >= 0) {
        status = BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0);
    }

    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0) {
        return {};
    }

    std::ostringstream stream;
    stream << std::hex << std::setfill('0');
    for (const unsigned char value : digest) {
        stream << std::setw(2) << static_cast<int>(value);
    }

    return stream.str();
}
#else
std::string sha256_hex(const std::vector<unsigned char>& bytes) {
    (void)bytes;
    return {};
}
#endif

unsigned short crc16_ccitt(const std::vector<unsigned char>& bytes, size_t offset, size_t length) {
    unsigned int crc = 0;
    const size_t end = (std::min)(bytes.size(), offset + length);
    for (size_t i = offset; i < end; ++i) {
        crc ^= static_cast<unsigned int>(bytes[i]) << 8;
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 0x8000) != 0
                ? ((crc << 1) ^ 0x1021) & 0xffff
                : (crc << 1) & 0xffff;
        }
    }

    return static_cast<unsigned short>(crc & 0xffff);
}

int ddr4_density_mbit(unsigned char code) {
    switch (code & 0x0f) {
    case 0x00:
        return 256;
    case 0x01:
        return 512;
    case 0x02:
        return 1024;
    case 0x03:
        return 2048;
    case 0x04:
        return 4096;
    case 0x05:
        return 8192;
    case 0x06:
        return 16384;
    case 0x07:
        return 32768;
    default:
        return 0;
    }
}

int ddr4_device_width_bits(unsigned char value) {
    switch (value & 0x07) {
    case 0:
        return 4;
    case 1:
        return 8;
    case 2:
        return 16;
    case 3:
        return 32;
    default:
        return 0;
    }
}

int ddr4_bus_width_bits(unsigned char value) {
    switch (value & 0x07) {
    case 0:
        return 8;
    case 1:
        return 16;
    case 2:
        return 32;
    case 3:
        return 64;
    default:
        return 0;
    }
}

int ddr4_bank_groups(unsigned char value) {
    switch ((value >> 6) & 0x03) {
    case 0:
        return 0;
    case 1:
        return 2;
    case 2:
        return 4;
    default:
        return 0;
    }
}

std::string ddr4_module_type(unsigned char value) {
    switch (value & 0x0f) {
    case 0x01:
        return "RDIMM";
    case 0x02:
        return "UDIMM";
    case 0x03:
        return "SO-DIMM";
    case 0x04:
        return "LRDIMM";
    case 0x08:
        return "72b SO-RDIMM";
    case 0x09:
        return "72b SO-UDIMM";
    case 0x0c:
        return "16b SO-DIMM";
    case 0x0d:
        return "32b SO-DIMM";
    default:
        return "DDR4 Module";
    }
}

int first_ddr4_cas_latency(const std::vector<unsigned char>& bytes) {
    if (bytes.size() <= 23) {
        return 0;
    }

    unsigned int mask = static_cast<unsigned int>(bytes[20]) |
        (static_cast<unsigned int>(bytes[21]) << 8) |
        (static_cast<unsigned int>(bytes[22]) << 16) |
        (static_cast<unsigned int>(bytes[23]) << 24);
    for (int bit = 0; bit < 32; ++bit) {
        if ((mask & (1u << bit)) != 0) {
            return bit + 7;
        }
    }

    return 0;
}

ParseResult parse_ddr4_spd(const std::vector<unsigned char>& bytes, const std::string& locator) {
    ParseResult result;
    result.status = "ok";
    if (bytes.size() < 384) {
        result.status = "parseFailed";
        result.diagnostics.push_back("DDR4 SPD image is shorter than 384 bytes.");
        return result;
    }

    ParsedSpdModule module;
    module.locator = locator.empty() ? "DIMM 0" : locator;
    module.memory_type = "DDR4";
    module.module_type = ddr4_module_type(bytes[3]);
    module.raw_byte_count = static_cast<int>(bytes.size());
    module.raw_sha256 = sha256_hex(bytes);
    module.revision = std::to_string((bytes[1] >> 4) & 0x0f) + "." + std::to_string(bytes[1] & 0x0f);
    module.manufacturer = manufacturer_id_hex(bytes, 320);
    module.dram_manufacturer = manufacturer_id_hex(bytes, 350);
    module.manufacturing_week = bytes[323];
    module.manufacturing_year = bytes[324] > 0 ? 2000 + bytes[324] : 0;
    module.serial_number = serial_hex(bytes, 325);
    module.part_number = read_spd_ascii(bytes, 329, 20);

    module.organization.rank_count = ((bytes[12] >> 3) & 0x07) + 1;
    module.organization.device_width_bits = ddr4_device_width_bits(bytes[12]);
    module.organization.bus_width_bits = ddr4_bus_width_bits(bytes[13]);
    module.organization.data_width_bits = module.organization.bus_width_bits;
    module.organization.total_width_bits = module.organization.bus_width_bits + (((bytes[13] >> 3) & 0x03) * 8);
    module.organization.bank_group_count = ddr4_bank_groups(bytes[4]);
    module.organization.banks_per_group = 4;

    const int density_mbit = ddr4_density_mbit(bytes[4]);
    if (density_mbit > 0 &&
        module.organization.rank_count > 0 &&
        module.organization.device_width_bits > 0 &&
        module.organization.bus_width_bits > 0) {
        module.capacity_bytes =
            (static_cast<unsigned long long>(density_mbit) * 1024ull * 1024ull / 8ull) *
            static_cast<unsigned long long>(module.organization.bus_width_bits / module.organization.device_width_bits) *
            static_cast<unsigned long long>(module.organization.rank_count);
    }

    module.voltages.vdd_mv = 1200;
    module.voltages.vddq_mv = 1200;
    module.voltages.vpp_mv = 2500;

    const unsigned short expected_crc = static_cast<unsigned short>(bytes[126]) |
        (static_cast<unsigned short>(bytes[127]) << 8);
    const unsigned short actual_crc = crc16_ccitt(bytes, 0, 126);
    module.crc_ok = expected_crc == actual_crc;
    module.checksum_ok = module.crc_ok;
    if (!module.crc_ok) {
        result.status = "checksumFailed";
        module.diagnostics.push_back("DDR4 SPD base configuration CRC mismatch.");
    }

    const int tck_mtb = bytes.size() > 18 ? bytes[18] : 0;
    const int cl = first_ddr4_cas_latency(bytes);
    if (tck_mtb > 0) {
        const double tck_ns = static_cast<double>(tck_mtb) * 0.125;
        const double frequency = 1000.0 / tck_ns;
        TimingProfile profile;
        profile.name = "JEDEC";
        profile.kind = "jedec";
        profile.frequency_mhz = frequency;
        profile.effective_rate_mtps = static_cast<int>(frequency * 2.0 + 0.5);
        profile.cas_latency = cl;
        profile.trcd = bytes.size() > 24 ? bytes[24] : 0;
        profile.trp = bytes.size() > 26 ? bytes[26] : 0;
        profile.voltage_mv = 1200;
        module.timing_profiles.push_back(profile);
    }

    module.features.emplace_back("SPD Revision", module.revision);
    module.features.emplace_back("Raw Parser", "DDR4 first-pass");
    module.diagnostics.push_back("Parsed DDR4 raw SPD fixture bytes.");
    result.modules.push_back(std::move(module));
    return result;
}

ParseResult parse_spd_bytes(const std::vector<unsigned char>& bytes, const std::string& locator) {
    ParseResult result;
    if (bytes.size() < 3) {
        result.status = "parseFailed";
        result.diagnostics.push_back("SPD image is too short to identify memory technology.");
        return result;
    }

    if (bytes[2] == 0x0c) {
        return parse_ddr4_spd(bytes, locator);
    }

    if (bytes[2] == 0x12) {
        result.status = "unsupportedMemoryType";
        result.diagnostics.push_back("DDR5 raw SPD parser is not implemented yet; use payload fixture until DDR5 byte layout support lands.");
        return result;
    }

    result.status = "unsupportedMemoryType";
    result.diagnostics.push_back("Unsupported SPD memory technology byte: 0x" + hex_byte(bytes[2]) + ".");
    return result;
}

void print_diagnostic_list(std::ostream& os, const std::vector<std::string>& diagnostics) {
    os << ",\"diagnostics\":[";
    for (size_t i = 0; i < diagnostics.size(); ++i) {
        if (i > 0) {
            os << ',';
        }
        print_json_string(os, diagnostics[i]);
    }
    os << ']';
}

void print_features_json(std::ostream& os, const std::vector<std::pair<std::string, std::string>>& features) {
    os << ",\"features\":[";
    for (size_t i = 0; i < features.size(); ++i) {
        if (i > 0) {
            os << ',';
        }

        os << "{\"name\":";
        print_json_string(os, features[i].first);
        os << ",\"value\":";
        print_json_string(os, features[i].second);
        os << '}';
    }
    os << ']';
}

void print_timing_profiles_json(std::ostream& os, const std::vector<TimingProfile>& profiles) {
    os << ",\"timingProfiles\":[";
    for (size_t i = 0; i < profiles.size(); ++i) {
        if (i > 0) {
            os << ',';
        }

        const auto& profile = profiles[i];
        os << "{\"name\":";
        print_json_string(os, profile.name);
        os << ",\"kind\":";
        print_json_string(os, profile.kind);
        os << ",\"frequencyMHz\":" << std::fixed << std::setprecision(1) << profile.frequency_mhz
           << ",\"effectiveRateMTps\":" << profile.effective_rate_mtps
           << ",\"casLatency\":" << profile.cas_latency
           << ",\"trcd\":" << profile.trcd
           << ",\"trp\":" << profile.trp
           << ",\"tras\":" << profile.tras
           << ",\"trc\":" << profile.trc
           << ",\"voltageMv\":" << profile.voltage_mv
           << '}';
    }
    os << ']';
}

void print_module_json(std::ostream& os, const ParsedSpdModule& module) {
    os << "{\"locator\":";
    print_json_string(os, module.locator);
    os << ",\"type\":";
    print_json_string(os, module.memory_type);
    os << ",\"moduleType\":";
    print_json_string(os, module.module_type);
    os << ",\"capacityBytes\":" << module.capacity_bytes;
    os << ",\"manufacturer\":";
    print_json_string(os, module.manufacturer);
    os << ",\"dramManufacturer\":";
    print_json_string(os, module.dram_manufacturer);
    os << ",\"partNumber\":";
    print_json_string(os, module.part_number);
    os << ",\"serialNumber\":";
    print_json_string(os, module.serial_number);
    os << ",\"manufacturingWeek\":" << module.manufacturing_week
       << ",\"manufacturingYear\":" << module.manufacturing_year;
    os << ",\"revision\":";
    print_json_string(os, module.revision);
    os << ",\"organization\":{"
       << "\"rankCount\":" << module.organization.rank_count
       << ",\"bankGroupCount\":" << module.organization.bank_group_count
       << ",\"banksPerGroup\":" << module.organization.banks_per_group
       << ",\"deviceWidthBits\":" << module.organization.device_width_bits
       << ",\"busWidthBits\":" << module.organization.bus_width_bits
       << ",\"dataWidthBits\":" << module.organization.data_width_bits
       << ",\"totalWidthBits\":" << module.organization.total_width_bits
       << '}';
    os << ",\"voltages\":{"
       << "\"vddMv\":" << module.voltages.vdd_mv
       << ",\"vddqMv\":" << module.voltages.vddq_mv
       << ",\"vppMv\":" << module.voltages.vpp_mv
       << '}';
    os << ",\"raw\":{"
       << "\"byteCount\":" << module.raw_byte_count
       << ",\"checksumOk\":" << (module.checksum_ok ? "true" : "false")
       << ",\"crcOk\":" << (module.crc_ok ? "true" : "false")
       << ",\"sha256\":";
    print_json_string(os, module.raw_sha256);
    os << '}';
    print_timing_profiles_json(os, module.timing_profiles);
    print_features_json(os, module.features);
    print_diagnostic_list(os, module.diagnostics);
    os << '}';
}

void print_parse_result_json(std::ostream& os, const ParseResult& result, std::string_view backend) {
    os << "{\"schemaVersion\":" << kSchemaVersion
       << ",\"workerVersion\":";
    print_json_string(os, kWorkerVersion);
    os << ",\"backend\":";
    print_json_string(os, backend);
    os << ",\"status\":";
    print_json_string(os, result.status);
    os << ",\"modules\":[";
    for (size_t i = 0; i < result.modules.size(); ++i) {
        if (i > 0) {
            os << ',';
        }

        print_module_json(os, result.modules[i]);
    }
    os << ']';
    print_diagnostic_list(os, result.diagnostics);
    os << "}\n";
}

void print_not_implemented_json(std::ostream& os) {
    os << "{\"schemaVersion\":" << kSchemaVersion
       << ",\"workerVersion\":";
    print_json_string(os, kWorkerVersion);
    os << ",\"backend\":\"none\""
       << ",\"status\":\"notImplemented\""
       << ",\"modules\":[]";
    print_diagnostic_list(os, {
        "SPD hardware acquisition is not implemented.",
        "Offline SPD parsing remains available through the fixture backend."
    });
    os << "}\n";
}

void print_fixture_json(std::ostream& os, const std::string& fixture_path) {
    if (fixture_path.empty()) {
        throw std::invalid_argument("--backend fixture requires --fixture PATH");
    }

    const std::string fixture = read_all_text(fixture_path);
    const std::string bytes_hex = extract_json_string(fixture, "bytesHex");
    if (!bytes_hex.empty()) {
        const std::string locator = extract_json_string(fixture, "locator");
        ParseResult result;
        try {
            const auto bytes = parse_hex_bytes(bytes_hex);
            result = parse_spd_bytes(bytes, locator);
        } catch (const std::exception& ex) {
            result.status = "parseFailed";
            result.diagnostics.push_back(std::string("Raw SPD fixture parse failed: ") + ex.what());
        }

        result.diagnostics.push_back("Parsed raw SPD bytes from fixture: " + fixture_path);
        print_parse_result_json(os, result, "fixture");
        return;
    }

    // Legacy fixture files are already worker-payload-shaped JSON. Keeping this
    // pass-through preserves existing UI/Core integration fixtures.
    os << fixture;
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
            print_not_implemented_json(std::cout);
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
