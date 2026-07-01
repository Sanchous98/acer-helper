using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AcerHelper.Vendors.Generic;

// Low-level WMI COM interop, done with SOURCE-GENERATED COM ([GeneratedComInterface]) + raw VARIANT/
// SAFEARRAY pointers so the whole thing is Native-AOT-safe. This is why we don't use System.Management:
// it relies on classic COM interop (runtime-generated IL marshalling stubs), which Native AOT cannot do
// (it throws "COM Interop is not supported on this platform"). The source generator here emits the vtable
// dispatch at compile time instead, so no runtime codegen is needed.
//
// CRITICAL: the methods in each interface are declared in EXACT vtable order (from wbemcli.h). The vtable
// slot of a method = its declaration position (after IUnknown's QueryInterface/AddRef/Release). Methods we
// don't call are declared as ordered placeholders purely to occupy their slot — DO NOT reorder, rename, or
// delete any method, and add new ones only in their correct wbemcli.h position. A wrong slot is a silent
// wrong-function call (crash / garbage), invisible until it runs on real hardware.
//
// All string parameters are passed as BSTR handles (nint) that the caller allocates with SysAllocString and
// frees with SysFreeString — a BSTR is null-terminated UTF-16, so it also serves where the API wants a plain
// LPCWSTR (Get/Put property names). VARIANT/CIMTYPE out-params are passed as raw pointers to caller locals.

internal static partial class Wbem
{
    // ---- GUIDs (wbemcli.h) ----
    internal static readonly Guid CLSID_WbemLocator = new("4590f811-1d3a-11d0-891f-00aa004b2e24");
    internal static readonly Guid IID_IWbemLocator  = new("dc12a687-737f-11cf-884d-00aa004b2e24");

    // ---- CLSCTX / CoInit ----
    internal const uint CLSCTX_INPROC_SERVER = 0x1;
    internal const uint COINIT_MULTITHREADED = 0x0;

    // ---- CoSetProxyBlanket auth (the standard WMI blanket) ----
    internal const uint RPC_C_AUTHN_WINNT = 10;
    internal const uint RPC_C_AUTHZ_NONE = 0;
    internal const uint RPC_C_AUTHN_LEVEL_CALL = 3;
    internal const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
    internal const uint EOAC_NONE = 0;

    // ---- HRESULTs ----
    internal const int S_OK = 0;
    internal const int WBEM_S_NO_ERROR = 0;
    internal const int WBEM_S_FALSE = 0x40006;   // enumerator exhausted

    // ---- VARTYPE ----
    internal const ushort VT_EMPTY = 0, VT_NULL = 1, VT_I2 = 2, VT_I4 = 3, VT_BSTR = 8, VT_BOOL = 11,
        VT_I1 = 16, VT_UI1 = 17, VT_UI2 = 18, VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_INT = 22,
        VT_UINT = 23, VT_ARRAY = 0x2000;

    // ---- CIMTYPE (for IWbemClassObject::Get/Put) ----
    internal const int CIM_UINT8 = 17;
    // WMI marshals 64-bit CIM integers as VT_BSTR (decimal string) in VARIANTs — not VT_UI8/VT_I8. So a
    // property of these types must be read by parsing the string and written by passing a VT_BSTR.
    internal const int CIM_SINT64 = 20;
    internal const int CIM_UINT64 = 21;

    // ComWrappers instance that turns a raw COM pointer into a callable RCW for our [GeneratedComInterface]
    // types. UniqueInstance RCWs are IDisposable, so we release the underlying COM reference deterministically.
    internal static readonly StrategyBasedComWrappers ComWrappers = new();

    // ---- ole32 / oleaut32 ----
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext,
        in Guid riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    internal static partial int CoSetProxyBlanket(nint pProxy, uint authnSvc, uint authzSvc,
        nint serverPrincName, uint authnLevel, uint impLevel, nint authInfo, uint capabilities);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SysAllocString(string s);

    [LibraryImport("oleaut32.dll")]
    internal static partial void SysFreeString(nint bstr);

    [LibraryImport("oleaut32.dll")]
    internal static partial nint SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayAccessData(nint psa, out nint ppvData);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayUnaccessData(nint psa);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetUBound(nint psa, uint nDim, out int lUbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetLBound(nint psa, uint nDim, out int lLbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayDestroy(nint psa);

    [LibraryImport("oleaut32.dll")]
    internal static partial int VariantClear(nint pvarg);
}

/// <summary>A 24-byte VARIANT (x64 layout: 8-byte header incl. the VARTYPE, 16-byte union). Only the
/// members we actually read/write are declared; the explicit Size=24 keeps the buffer big enough for the
/// callee to write any VARIANT (e.g. a BSTR or SAFEARRAY pointer in the union) without overrunning.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public nint ptrVal;    // BSTR (VT_BSTR) or SAFEARRAY* (VT_ARRAY)
    [FieldOffset(8)] public long llVal;
    [FieldOffset(8)] public ulong ullVal;
    [FieldOffset(8)] public int lVal;
    [FieldOffset(8)] public uint ulVal;
    [FieldOffset(8)] public short iVal;
    [FieldOffset(8)] public ushort uiVal;
    [FieldOffset(8)] public sbyte cVal;
    [FieldOffset(8)] public byte bVal;
}

// ---- WMI COM interfaces (methods in exact wbemcli.h vtable order; see the header note) ----

[GeneratedComInterface, Guid("dc12a687-737f-11cf-884d-00aa004b2e24")]
internal partial interface IWbemLocator
{
    [PreserveSig] int ConnectServer(nint strNetworkResource, nint strUser, nint strPassword,
        nint strLocale, int lSecurityFlags, nint strAuthority, nint pCtx, out nint ppNamespace);
}

[GeneratedComInterface, Guid("9556dc99-828c-11cf-a37e-00aa003240c7")]
internal partial interface IWbemServices
{
    [PreserveSig] int OpenNamespace();            // slot 3  (placeholder)
    [PreserveSig] int CancelAsyncCall();          // slot 4  (placeholder)
    [PreserveSig] int QueryObjectSink();          // slot 5  (placeholder)
    [PreserveSig] int GetObject(nint strObjectPath, int lFlags, nint pCtx,
        out IWbemClassObject? ppObject, nint ppCallResult);   // slot 6 — fetch a class definition (for GetMethod)
    [PreserveSig] int GetObjectAsync();           // slot 7  (placeholder)
    [PreserveSig] int PutClass();                 // slot 8  (placeholder)
    [PreserveSig] int PutClassAsync();            // slot 9  (placeholder)
    [PreserveSig] int DeleteClass();              // slot 10 (placeholder)
    [PreserveSig] int DeleteClassAsync();         // slot 11 (placeholder)
    [PreserveSig] int CreateClassEnum();          // slot 12 (placeholder)
    [PreserveSig] int CreateClassEnumAsync();     // slot 13 (placeholder)
    [PreserveSig] int PutInstance();              // slot 14 (placeholder)
    [PreserveSig] int PutInstanceAsync();         // slot 15 (placeholder)
    [PreserveSig] int DeleteInstance();           // slot 16 (placeholder)
    [PreserveSig] int DeleteInstanceAsync();      // slot 17 (placeholder)
    [PreserveSig] int CreateInstanceEnum();       // slot 18 (placeholder)
    [PreserveSig] int CreateInstanceEnumAsync();  // slot 19 (placeholder)
    [PreserveSig] int ExecQuery(nint strQueryLanguage, nint strQuery, int lFlags, nint pCtx,
        out IEnumWbemClassObject? ppEnum);        // slot 20
    [PreserveSig] int ExecQueryAsync();           // slot 21 (placeholder)
    [PreserveSig] int ExecNotificationQuery();    // slot 22 (placeholder)
    [PreserveSig] int ExecNotificationQueryAsync();// slot 23 (placeholder)
    [PreserveSig] int ExecMethod(nint strObjectPath, nint strMethodName, int lFlags, nint pCtx,
        IWbemClassObject? pInParams, out IWbemClassObject? ppOutParams, nint ppCallResult);   // slot 24
}

[GeneratedComInterface, Guid("dc12a681-737f-11cf-884d-00aa004b2e24")]
internal partial interface IWbemClassObject
{
    [PreserveSig] int GetQualifierSet();          // slot 3  (placeholder)
    [PreserveSig] int Get(nint wszName, int lFlags, nint pVal, nint pType, nint plFlavor);  // slot 4
    [PreserveSig] int Put(nint wszName, int lFlags, nint pVal, int type);                   // slot 5
    [PreserveSig] int Delete();                   // slot 6  (placeholder)
    [PreserveSig] int GetNames();                 // slot 7  (placeholder)
    [PreserveSig] int BeginEnumeration();         // slot 8  (placeholder)
    [PreserveSig] int Next();                     // slot 9  (placeholder)
    [PreserveSig] int EndEnumeration();           // slot 10 (placeholder)
    [PreserveSig] int GetPropertyQualifierSet();  // slot 11 (placeholder)
    [PreserveSig] int Clone();                    // slot 12 (placeholder)
    [PreserveSig] int GetObjectText();            // slot 13 (placeholder)
    [PreserveSig] int SpawnDerivedClass();        // slot 14 (placeholder)
    [PreserveSig] int SpawnInstance(int lFlags, out IWbemClassObject? ppNewInstance);        // slot 15
    [PreserveSig] int CompareTo();                // slot 16 (placeholder)
    [PreserveSig] int GetPropertyOrigin();        // slot 17 (placeholder)
    [PreserveSig] int InheritsFrom();             // slot 18 (placeholder)
    [PreserveSig] int GetMethod(nint wszName, int lFlags, out IWbemClassObject? ppInSignature,
        out IWbemClassObject? ppOutSignature);    // slot 19
}

[GeneratedComInterface, Guid("027947e1-d731-11ce-a357-000000000001")]
internal partial interface IEnumWbemClassObject
{
    [PreserveSig] int Reset();                    // slot 3  (placeholder)
    [PreserveSig] int Next(int lTimeout, uint uCount, out IWbemClassObject? apObjects,
        out uint puReturned);                     // slot 4
}
