#pragma once

#if defined(DEPMODULE_EXPORTS)
  #define DEP_API __declspec(dllexport)
#else
  #define DEP_API __declspec(dllimport)
#endif

extern "C" DEP_API int DepModuleGetValue();
