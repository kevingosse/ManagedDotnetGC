// dllmain.cpp : Defines the entry point for the DLL application.
#include "pch.h"

#include <iostream>
#include <windows.h>
#include <stdlib.h>

typedef HRESULT(__stdcall* f_GC_Initialize)(void*, void*, void*, void*);
typedef HRESULT(__stdcall* f_GC_VersionInfo)(void*);

static f_GC_Initialize s_gcInitialize;
static f_GC_VersionInfo s_gcVersionInfo;

BOOL APIENTRY DllMain(HMODULE hModule,
	DWORD  ul_reason_for_call,
	LPVOID lpReserved
)
{
	if (ul_reason_for_call == 0)
	{
		std::cout << "[Loader] Detaching ManagedDotnetGC loader" << std::endl;
		return true;
	}
	else if (ul_reason_for_call != 1)
	{
		return true;
	}

	std::cout << "[Loader] Loading module" << std::endl;
	auto module = LoadLibraryA("ManagedDotnetGC.dll");

	if (module == 0)
	{
		std::cout << "Could not find ManagedDotnetGC.dll" << std::endl;
		return FALSE;
	}

	std::cout << "[Loader] Successfully loaded ManagedDotnetGC.dll" << std::endl;

	s_gcInitialize = (f_GC_Initialize)GetProcAddress(module, "Custom_GC_Initialize");
	s_gcVersionInfo = (f_GC_VersionInfo)GetProcAddress(module, "Custom_GC_VersionInfo");

	return true;
}

extern "C" __declspec(dllexport) HRESULT GC_Initialize(
	void* clrToGC,
	void** gcHeap,
	void** gcHandleManager,
	void* gcDacVars
)
{
	std::cout << "[Loader] Forwarding call to GC_Initialize" << std::endl;
	return s_gcInitialize(clrToGC, gcHeap, gcHandleManager, gcDacVars);
}

extern "C" __declspec(dllexport) void GC_VersionInfo(void* result)
{
	std::cout << "[Loader] Fowarding call to GC_VersionInfo" << std::endl;
	s_gcVersionInfo(result);
}

