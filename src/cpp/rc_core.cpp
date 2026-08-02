// rc_core.cpp - API glue / shared helpers. All real work lives in the
// other translation units; this file just exposes rc_free and pins the
// export macro so every function is visible from C# (P/Invoke).

#include "rc_core.h"
extern "C" {
#include <libavutil/mem.h>
}

extern "C" {

void rc_free(uint8_t* p) {
    if (p) av_free(p);
}

} // extern "C"
