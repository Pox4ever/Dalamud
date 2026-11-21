using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

using Dalamud.Configuration.Internal;
using Dalamud.Hooking;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Internal;

/// <summary>
/// Hides Dalamud from OBS Game Capture by drawing underneath OBS's hook.
/// </summary>
/// <remarks>
/// OBS Game Capture hooks the code of the system dxgi.dll IDXGISwapChain::Present. With "Capture third-party overlays"
/// disabled, it copies the back buffer before calling into whatever was hooked before it. If Dalamud hooks that same
/// function before OBS does, OBS copies the frame before Dalamud has drawn on it, which makes Dalamud behave like an
/// overlay (Steam, Discord). Whoever hooks last runs first, so the hook has to be installed as early as possible:
/// at Dalamud startup, before the game creates its window, which is before OBS can inject. The address is learned
/// from the game's swap chain on a previous launch and cached in the configuration.
/// </remarks>
internal unsafe partial class InterfaceManager
{
    private const int DxgiSwapChainPresentVtblIndex = 8;
    private const int DxgiSwapChainVtblEntryCount = 18;

    private static readonly DxgiSwapChainPresentDelegate CaptureHidingPresentDelegate = CaptureHidingPresentDetour;

    private static readonly object CaptureHidingSyncRoot = new();

    private static Hook<DxgiSwapChainPresentDelegate>? captureHidingPresentHook;
    private static nint captureHidingTarget;
    private static InterfaceManager? captureHidingOwner;

    private static string SystemDxgiPath => Path.Combine(Environment.SystemDirectory, "dxgi.dll");

    /// <summary>Starts a background thread that installs the capture hiding hook as soon as the system dxgi.dll is
    /// loaded, if the feature is enabled and the location of Present has been learned on a previous launch.</summary>
    /// <param name="configuration">The Dalamud configuration.</param>
    internal static void StartEarlyCaptureHidingHook(DalamudConfiguration configuration)
    {
        if (!configuration.HideDalamudFromGameCapture)
        {
            Log.Information("[CaptureHiding] Option is off; Dalamud will be hooked the normal way");
            return;
        }

        if (configuration.SwapChainHookMode != SwapChainHelper.HookMode.ByteCode)
        {
            Log.Warning("[CaptureHiding] Disabled: requires the bytecode swap chain hook mode");
            return;
        }

        if (!TryParseCache(configuration.GameCaptureHidingDxgiPresentCache, out var timeDateStamp, out var sizeOfImage, out var presentRva))
        {
            Log.Information("[CaptureHiding] No cached Present location yet; it will be learned at the title screen and used from the next launch");
            return;
        }

        new Thread(() => EarlyHookThreadBody(timeDateStamp, sizeOfImage, presentRva))
        {
            Name = "Dalamud CaptureHiding",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        }.Start();
    }

    private static void EarlyHookThreadBody(uint timeDateStamp, uint sizeOfImage, uint presentRva)
    {
        try
        {
            var path = SystemDxgiPath;
            nint dxgiBase = 0;
            var deadline = Environment.TickCount64 + 120_000;
            while ((dxgiBase = (nint)global::Windows.Win32.PInvoke.GetModuleHandle(path).Value) == 0)
            {
                if (Environment.TickCount64 > deadline)
                {
                    Log.Warning("[CaptureHiding] System dxgi.dll was not loaded within 2 minutes; giving up on the early hook");
                    return;
                }

                Thread.Sleep(1);
            }

            // Give the loader a moment to finish relocating and initializing the module.
            Thread.Sleep(10);

            var nt = GetNtHeaders(dxgiBase);
            if (nt->FileHeader.TimeDateStamp != timeDateStamp || nt->OptionalHeader.SizeOfImage != sizeOfImage)
            {
                Log.Warning("[CaptureHiding] dxgi.dll changed since the Present location was learned (Windows update?); it will be re-learned at the title screen");
                return;
            }

            var address = dxgiBase + (nint)presentRva;
            InstallCaptureHidingHook(address, "early");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CaptureHiding] Failed to install the early hook");
        }
    }

    private static void InstallCaptureHidingHook(nint address, string when)
    {
        lock (CaptureHidingSyncRoot)
        {
            if (captureHidingPresentHook is not null && captureHidingTarget == address)
                return;

            var hookAt = address;
            if (TryFindObsDetoursTrampoline(address, out var trampolineCode))
            {
                // OBS hooked Present before us (e.g. Dalamud was injected into a running game). OBS calls the original
                // Present through its Detours trampoline after copying the frame, so hooking the trampoline puts
                // Dalamud underneath OBS. OBS never removes its hooks, so the trampoline stays valid.
                Log.Information(
                    "[CaptureHiding] OBS already hooked Present; hooking its trampoline at {tramp} to stay underneath it",
                    Util.DescribeAddress(trampolineCode));
                hookAt = trampolineCode;
            }
            else if (IsCodePatched(address))
            {
                Log.Warning(
                    "[CaptureHiding] dxgi Present at {addr} is already hooked by something that is not OBS Game Capture; Dalamud may still show up in captures this session",
                    Util.DescribeAddress(address));
            }

            var hook = Hook<DxgiSwapChainPresentDelegate>.FromAddress(hookAt, CaptureHidingPresentDelegate);
            captureHidingPresentHook = hook;
            captureHidingTarget = address;
            hook.Enable();
            Log.Information(
                "[CaptureHiding] Hooked system dxgi IDXGISwapChain::Present ({when}) at {addr}",
                when,
                Util.DescribeAddress(hookAt));
        }
    }

    /// <summary>Detects an OBS graphics-hook (Microsoft Detours, x64) hook on <paramref name="target"/>, and returns the
    /// address of the trampoline code OBS calls to reach the original function.</summary>
    /// <remarks>
    /// Detours patches the target with <c>jmp rel32</c> to <c>rbCodeIn</c> of a 96-byte <c>_DETOUR_TRAMPOLINE</c>:
    /// rbCode[30] @0, cbCode @30, cbCodeBreak @31, rbRestore[30] @32, cbRestore @62, cbRestoreBreak @63,
    /// rAlign[8] @64, pbRemain @72, pbDetour @80, rbCodeIn[8] @88 (= <c>jmp [rip-14]</c>, i.e. <c>jmp [pbDetour]</c>).
    /// </remarks>
    private static bool TryFindObsDetoursTrampoline(nint target, out nint trampolineCode)
    {
        trampolineCode = 0;
        try
        {
            var p = (byte*)target;
            if (p[0] != 0xE9)
                return false;

            var codeIn = (byte*)(target + 5 + *(int*)(p + 1));
            if (codeIn[0] != 0xFF || codeIn[1] != 0x25 || *(int*)(codeIn + 2) != -14)
                return false;

            var tramp = codeIn - 88;
            var cbRestore = tramp[62];
            var pbRemain = *(nint*)(tramp + 72);
            var pbDetour = *(nint*)(tramp + 80);
            if (cbRestore is < 5 or > 30 || pbRemain != target + cbRestore)
                return false;

            var module = GetModuleFileNameContaining(pbDetour);
            if (module is null || !Path.GetFileName(module).StartsWith("graphics-hook", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information(
                    "[CaptureHiding] Present is hooked with Detours by {module}, not OBS; not chaining under it",
                    module ?? "<unknown>");
                return false;
            }

            trampolineCode = (nint)tramp;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CaptureHiding] Failed to inspect the existing hook on Present");
            return false;
        }
    }

    private static string? GetModuleFileNameContaining(nint address)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule m in process.Modules)
        {
            if (address >= m.BaseAddress && address < m.BaseAddress + m.ModuleMemorySize)
                return m.FileName;
        }

        return null;
    }

    private static int CaptureHidingPresentDetour(IDXGISwapChain* swapChain, uint syncInterval, uint flags)
    {
        var hook = captureHidingPresentHook!;
        if (captureHidingOwner is { } owner && owner.RenderDalamudCheckAndInitialize(swapChain, flags) is { } activeBackend)
            owner.RenderDalamudDraw(activeBackend);

        return hook.Original(swapChain, syncInterval, flags);
    }

    private static void ClearCaptureHidingHook()
    {
        lock (CaptureHidingSyncRoot)
        {
            captureHidingOwner = null;
            captureHidingTarget = 0;
            Interlocked.Exchange(ref captureHidingPresentHook, null)?.Dispose();
        }
    }

    private static bool TryParseCache(string? cache, out uint timeDateStamp, out uint sizeOfImage, out uint presentRva)
    {
        timeDateStamp = sizeOfImage = presentRva = 0;
        var parts = cache?.Split(':');
        return parts is { Length: 3 } &&
               uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out timeDateStamp) &&
               uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out sizeOfImage) &&
               uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out presentRva);
    }

    private static IMAGE_NT_HEADERS64* GetNtHeaders(nint moduleBase)
    {
        var dos = (IMAGE_DOS_HEADER*)moduleBase;
        return (IMAGE_NT_HEADERS64*)(moduleBase + dos->e_lfanew);
    }

    /// <summary>Checks whether the first bytes of the function differ from the copy on disk.</summary>
    private static bool IsCodePatched(nint address)
    {
        try
        {
            var dxgiBase = (nint)global::Windows.Win32.PInvoke.GetModuleHandle(SystemDxgiPath).Value;
            if (dxgiBase == 0)
                return false;

            var file = File.ReadAllBytes(SystemDxgiPath);
            var offset = RvaToFileOffset(file, (uint)(address - dxgiBase));
            if (offset < 0 || offset + 5 > file.Length)
                return false;

            return !new ReadOnlySpan<byte>((void*)address, 5).SequenceEqual(file.AsSpan(offset, 5));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Finds the address of the system dxgi.dll IDXGISwapChain::Present code, ignoring any vtable patching done
    /// by other software (e.g. the Steam overlay), by reading the original vtable entry from dxgi.dll on disk.</summary>
    /// <param name="swapChain">An unwrapped (non-ReShade) DXGI swap chain.</param>
    /// <param name="address">The address of the original Present.</param>
    /// <param name="cache">The value to store in <see cref="DalamudConfiguration.GameCaptureHidingDxgiPresentCache"/>.</param>
    /// <returns><c>true</c> on success.</returns>
    private static bool TryResolveSystemDxgiPresent(IDXGISwapChain* swapChain, out nint address, out string cache)
    {
        address = 0;
        cache = string.Empty;

        var dxgiBase = (nint)global::Windows.Win32.PInvoke.GetModuleHandle(SystemDxgiPath).Value;
        if (dxgiBase == 0)
        {
            Log.Warning("[CaptureHiding] System dxgi.dll is not loaded");
            return false;
        }

        var nt = GetNtHeaders(dxgiBase);
        var sizeOfImage = nt->OptionalHeader.SizeOfImage;
        bool InDxgi(nint p) => p >= dxgiBase && p < dxgiBase + sizeOfImage;

        var vtbl = *(nint**)swapChain;
        nint vtblInDxgi;
        if (InDxgi((nint)vtbl))
        {
            vtblInDxgi = (nint)vtbl;
        }
        else
        {
            // Someone replaced the object's vtable pointer with their own copy. Find the original vtable in dxgi.dll by
            // matching the entries that still point into dxgi.dll.
            Log.Information("[CaptureHiding] Swap chain vtable {vtbl} is outside dxgi.dll; searching for the original", Util.DescribeAddress((nint)vtbl));
            vtblInDxgi = FindVtableInModule(dxgiBase, vtbl, InDxgi);
            if (vtblInDxgi == 0)
            {
                Log.Warning("[CaptureHiding] Could not find the original IDXGISwapChain vtable in dxgi.dll");
                return false;
            }
        }

        var file = File.ReadAllBytes(SystemDxgiPath);
        fixed (byte* pFile = file)
        {
            var fileNt = (IMAGE_NT_HEADERS64*)(pFile + ((IMAGE_DOS_HEADER*)pFile)->e_lfanew);
            if (fileNt->FileHeader.TimeDateStamp != nt->FileHeader.TimeDateStamp)
            {
                Log.Warning("[CaptureHiding] dxgi.dll on disk does not match the loaded one");
                return false;
            }

            var entryRva = (uint)(vtblInDxgi - dxgiBase) + (DxgiSwapChainPresentVtblIndex * 8);
            var offset = RvaToFileOffset(file, entryRva);
            if (offset < 0)
            {
                Log.Warning("[CaptureHiding] Could not map vtable RVA {rva:X} to the file", entryRva);
                return false;
            }

            var presentVa = *(ulong*)(pFile + offset);
            var presentRva = presentVa - fileNt->OptionalHeader.ImageBase;
            if (presentRva >= sizeOfImage)
            {
                Log.Warning("[CaptureHiding] Present RVA {rva:X} is outside dxgi.dll", presentRva);
                return false;
            }

            address = dxgiBase + (nint)presentRva;
            cache = string.Create(
                CultureInfo.InvariantCulture,
                $"{nt->FileHeader.TimeDateStamp:X8}:{sizeOfImage:X}:{presentRva:X}");
        }

        var current = vtbl[DxgiSwapChainPresentVtblIndex];
        Log.Information(
            "[CaptureHiding] dxgi Present is {addr}; current vtable entry is {cur}",
            Util.DescribeAddress(address),
            Util.DescribeAddress(current));
        return true;
    }

    private static nint FindVtableInModule(nint moduleBase, nint* vtbl, Func<nint, bool> inModule)
    {
        Span<nint> entries = stackalloc nint[DxgiSwapChainVtblEntryCount];
        var usable = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = vtbl[i];
            if (inModule(entries[i]))
                usable++;
        }

        if (usable < 8)
            return 0;

        var nt = GetNtHeaders(moduleBase);
        var sections = (IMAGE_SECTION_HEADER*)((byte*)&nt->OptionalHeader + nt->FileHeader.SizeOfOptionalHeader);
        for (var s = 0; s < nt->FileHeader.NumberOfSections; s++)
        {
            var sec = sections[s];
            const uint readable = 0x40000000; // IMAGE_SCN_MEM_READ
            const uint executable = 0x20000000; // IMAGE_SCN_MEM_EXECUTE
            if ((sec.Characteristics & readable) == 0 || (sec.Characteristics & executable) != 0)
                continue;

            var start = (nint*)(moduleBase + sec.VirtualAddress);
            var count = (int)(sec.Misc.VirtualSize / (uint)sizeof(nint)) - DxgiSwapChainVtblEntryCount;
            for (var p = 0; p < count; p++)
            {
                var match = true;
                for (var i = 0; i < DxgiSwapChainVtblEntryCount && match; i++)
                {
                    if (inModule(entries[i]))
                        match = start[p + i] == entries[i];
                }

                if (match)
                    return (nint)(start + p);
            }
        }

        return 0;
    }

    private static int RvaToFileOffset(byte[] file, uint rva)
    {
        fixed (byte* pFile = file)
        {
            var nt = (IMAGE_NT_HEADERS64*)(pFile + ((IMAGE_DOS_HEADER*)pFile)->e_lfanew);
            var sections = (IMAGE_SECTION_HEADER*)((byte*)&nt->OptionalHeader + nt->FileHeader.SizeOfOptionalHeader);
            for (var s = 0; s < nt->FileHeader.NumberOfSections; s++)
            {
                var sec = sections[s];
                var size = Math.Max(sec.Misc.VirtualSize, sec.SizeOfRawData);
                if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + size)
                {
                    var offset = rva - sec.VirtualAddress;
                    return offset < sec.SizeOfRawData ? (int)(sec.PointerToRawData + offset) : -1;
                }
            }
        }

        return -1;
    }

    /// <summary>Called from SetupHooks. Makes sure the capture hiding hook is installed on the system dxgi Present,
    /// learning and caching its location for the next launch.</summary>
    /// <returns><c>true</c> if Dalamud will draw from the capture hiding hook.</returns>
    private bool SetupCaptureHidingHook()
    {
        try
        {
            if (!TryResolveSystemDxgiPresent(SwapChainHelper.GameDeviceSwapChain, out var address, out var cache))
                return false;

            if (this.dalamudConfiguration.GameCaptureHidingDxgiPresentCache != cache)
            {
                this.dalamudConfiguration.GameCaptureHidingDxgiPresentCache = cache;
                this.dalamudConfiguration.QueueSave();
                Log.Information("[CaptureHiding] Learned Present location {cache}; it will be hooked at startup from the next launch", cache);
            }

            if (captureHidingPresentHook is not null)
            {
                if (captureHidingTarget == address)
                {
                    Log.Information("[CaptureHiding] Using the early hook");
                    return true;
                }

                Log.Warning(
                    "[CaptureHiding] Early hook targets {early}, but Present is at {actual}; re-hooking",
                    Util.DescribeAddress(captureHidingTarget),
                    Util.DescribeAddress(address));
                ClearCaptureHidingHook();
            }

            InstallCaptureHidingHook(address, "late");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CaptureHiding] Setup failed; falling back to the normal Present hook");
            ClearCaptureHidingHook();
            return false;
        }
    }
}
