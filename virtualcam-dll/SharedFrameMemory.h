#pragma once

#include <cstdint>

namespace PhoneCamShared {

constexpr uint32_t MAGIC = 0x50435643; // "PCVC"
constexpr uint32_t VERSION = 1;
constexpr uint32_t SLOT_SIZE = 4 * 1024 * 1024;
constexpr uint32_t SLOT_COUNT = 3;
constexpr size_t HEADER_SIZE = 64;
constexpr size_t TOTAL_SHM_SIZE = HEADER_SIZE + static_cast<size_t>(SLOT_SIZE) * SLOT_COUNT;

constexpr wchar_t SHM_DIR_PATH[] = L"C:\\ProgramData\\PhoneCam";
constexpr wchar_t SHM_FILE_PATH[] = L"C:\\ProgramData\\PhoneCam\\frame.shm";

// {7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}
constexpr wchar_t CLSID_PHONECAM_STR[] = L"{7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}";

#pragma pack(push, 1)
struct SharedHeader {
    uint32_t magic;
    uint32_t version;
    uint32_t slotSize;
    uint32_t slotCount;
    volatile int64_t writeSeq;
    volatile int32_t latestSlot;
    volatile int32_t frameWidth;
    volatile int32_t frameHeight;
    volatile int32_t requestedWidth;
    volatile int32_t requestedHeight;
    uint32_t reserved;
    volatile int64_t frameTimestampUs;
    volatile int64_t lastProducerTickMs;
};
#pragma pack(pop)

static_assert(sizeof(SharedHeader) == HEADER_SIZE, "SharedHeader must be 64 bytes");

} // namespace PhoneCamShared

