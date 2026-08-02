// memload.cpp - Phase 9: zzrat-inspired MemoryModulePP exports.
// Load a PE DLL from memory without touching the disk.
#include "rc_core.h"
#include "memory_loader.h"

extern "C" {

RC_API void* rc_memload_library(const uint8_t* data, int size) {
    if (!data || size <= 0) return nullptr;
    return MemoryLoader::Load(data, (size_t)size);
}

RC_API void* rc_memload_getproc(void* mod, const char* name) {
    if (!mod || !name) return nullptr;
    return MemoryLoader::GetProc(mod, name);
}

RC_API void rc_memload_free(void* mod) {
    MemoryLoader::Free(mod);
}

} // extern "C"
