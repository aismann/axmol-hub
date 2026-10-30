using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Native generic-password operations, injectable without accessing a user's keychain.</summary>
public interface IMacKeychainApi
{
    (int Status, string? Value) Read(string service, string account);
    int Add(string service, string account, string value);
    int Update(string service, string account, string value);
    int Delete(string service, string account);
}

/// <summary>Secrets isolated by data root in the current user's login keychain.</summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    public const int NotFound = -25300;
    public const int Duplicate = -25299;
    private readonly IMacKeychainApi _api;
    private string? _problem;
    public string Service { get; }
    public MacKeychainSecretStore(string dataRoot, IMacKeychainApi? api = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).Normalize(NormalizationForm.FormC);
        Service = "dev.axmol.hub." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
        _api = api ?? new NativeMacKeychainApi();
    }
    public SecretStoreDescriptor Descriptor => new(SecretStoreKind.Keychain, Service, _problem);
    public string? Read(string id)
    {
        var (status, value) = _api.Read(Service, id);
        if (status == NotFound) return null;
        Check(status);
        return value;
    }
    public void Write(string id, string value)
    {
        var status = _api.Update(Service, id, value);
        if (status == NotFound) status = _api.Add(Service, id, value);
        if (status == Duplicate) status = _api.Update(Service, id, value);
        Check(status);
    }
    public void Delete(string id)
    {
        var status = _api.Delete(Service, id);
        if (status != NotFound) Check(status);
    }
    private void Check(int status)
    {
        if (status == 0) { _problem = null; return; }
        _problem = $"Keychain operation failed (OSStatus {status}). Unlock the login keychain and allow Axmol Hub access, then retry.";
        throw new SecretStoreException(SecretStoreFailure.KeyUnusable, _problem);
    }
}

/// <summary>Core Foundation objects are released on every exit, including failed Security calls.</summary>
internal sealed class NativeMacKeychainApi : IMacKeychainApi
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr SecurityHandle = NativeLibrary.Load(Security);
    private static readonly IntPtr CFHandle = NativeLibrary.Load(CF);
    private static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
    private static IntPtr True => Marshal.ReadIntPtr(NativeLibrary.GetExport(CFHandle, "kCFBooleanTrue"));
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
    [DllImport(CF)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
    [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint count);
    [DllImport(CF)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);

    private sealed class Attributes : IDisposable
    {
        private readonly List<IntPtr> _owned = [];
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0,
            NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryValueCallBacks"));
        public Attributes(string? service = null, string? account = null)
        {
            if (service is null) return;
            Set("kSecClass", Constant("kSecClassGenericPassword"));
            Text("kSecAttrService", service);
            Text("kSecAttrAccount", account!);
        }
        public void Set(string key, IntPtr value) => CFDictionarySetValue(Handle, Constant(key), value);
        public void Text(string key, string value)
        {
            var pointer = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
            _owned.Add(pointer); Set(key, pointer);
        }
        public void Data(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                var pointer = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
                _owned.Add(pointer); Set("kSecValueData", pointer);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var pointer in _owned) CFRelease(pointer);
        }
    }
    public (int Status, string? Value) Read(string service, string account)
    {
        using var query = new Attributes(service, account);
        query.Set("kSecReturnData", True);
        query.Set("kSecMatchLimit", Constant("kSecMatchLimitOne"));
        var status = SecItemCopyMatching(query.Handle, out var result);
        try
        {
            if (status != 0 || result == IntPtr.Zero) return (status, null);
            var bytes = new byte[checked((int)CFDataGetLength(result))];
            try
            {
                Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                return (status, Encoding.UTF8.GetString(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (result != IntPtr.Zero) CFRelease(result); }
    }
    public int Add(string service, string account, string value)
    {
        using var attributes = new Attributes(service, account);
        attributes.Data(value);
        return SecItemAdd(attributes.Handle, IntPtr.Zero);
    }
    public int Update(string service, string account, string value)
    {
        using var query = new Attributes(service, account);
        using var attributes = new Attributes();
        attributes.Data(value);
        return SecItemUpdate(query.Handle, attributes.Handle);
    }
    public int Delete(string service, string account)
    {
        using var query = new Attributes(service, account);
        return SecItemDelete(query.Handle);
    }
}
