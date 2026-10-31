using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>
/// A password field. The value is stored encrypted with Windows DPAPI (bound to the Windows user), and the
/// settings page never shows it again: leaving the field empty keeps the stored value.
/// </summary>
public sealed class SecretSetting : Setting<string>
{
    private const string Prefix = "dpapi:";

    public SecretSetting(string key, string label, string description)
        : base(key, label, description, string.Empty)
    {
    }

    public bool HasValue => !string.IsNullOrEmpty(Value);

    public override Dictionary<string, object> ToDictionary() => new()
    {
        { "type", "Input.Text" },
        { "style", "Password" },
        { "title", Label },
        { "id", Key },
        { "label", Description },
        { "value", string.Empty },
        { "placeholder", HasValue ? "Stored – leave empty to keep it" : "Not set" },
    };

    /// <summary>
    /// Called with the saved file (encrypted value) and with the submitted form (typed plain text).
    /// An empty field keeps the stored value.
    /// </summary>
    public override void Update(JsonObject payload)
    {
        if (payload[Key]?.GetValue<string>() is not { Length: > 0 } stored)
        {
            return;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            Value = stored.Trim();
            return;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
            Value = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Error($"secret {Key} could not be decrypted", ex);
            Value = string.Empty;
        }
    }

    public override string ToState()
    {
        var stored = HasValue
            ? Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Value!), null, DataProtectionScope.CurrentUser))
            : string.Empty;
        return $"\"{Key}\": {JsonSerializer.Serialize(stored)}";
    }
}
