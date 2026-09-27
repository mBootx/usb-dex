namespace DexStream.Core.Device;

/// <summary>How confident we are that a connected USB device can stream DeX.</summary>
public enum DexSupportLevel
{
    /// <summary>Not a Samsung device, or not recognised at all.</summary>
    Unsupported,

    /// <summary>A Samsung device, but the model is not in the catalog.</summary>
    Unknown,

    /// <summary>A Samsung model known to ship DeX.</summary>
    Supported,
}

/// <param name="Level">Confidence that this device supports DeX.</param>
/// <param name="MarketingName">Human-readable model name when known.</param>
/// <param name="Notes">Anything the user should know about this model.</param>
public readonly record struct DexSupportInfo(DexSupportLevel Level, string? MarketingName, string? Notes);

/// <summary>
/// Maps USB vendor/product ids and Samsung model codes to DeX support.
/// </summary>
/// <remarks>
/// <para>
/// Samsung uses a small set of USB product ids that describe the <em>USB configuration</em> (MTP,
/// MTP+ADB, RNDIS, …) rather than the phone model, so the product id alone cannot identify a
/// handset. The model is therefore read from the <c>ro.product.model</c> property once ADB is up,
/// and the product id is used only to recognise that an ADB interface should be present.
/// </para>
/// <para>
/// DeX itself is available on every Galaxy S, Note, Z Fold and Tab flagship since the Galaxy S8, so
/// the catalog lists model-code prefixes rather than enumerating every SKU.
/// </para>
/// </remarks>
public static class SamsungDeviceCatalog
{
    /// <summary>Samsung Electronics' USB vendor id.</summary>
    public const int SamsungVendorId = 0x04E8;

    /// <summary>
    /// Model-code prefixes known to support DeX, mapped to their marketing name. Samsung model
    /// codes look like <c>SM-S938B</c>; the prefix identifies the family and generation.
    /// </summary>
    private static readonly (string Prefix, string Name)[] KnownModels =
    [
        // Galaxy S25 family (2025)
        ("SM-S931", "Galaxy S25"),
        ("SM-S936", "Galaxy S25+"),
        ("SM-S938", "Galaxy S25 Ultra"),
        ("SM-S937", "Galaxy S25 Edge"),
        // Galaxy S24 family
        ("SM-S921", "Galaxy S24"),
        ("SM-S926", "Galaxy S24+"),
        ("SM-S928", "Galaxy S24 Ultra"),
        // Galaxy S23 family
        ("SM-S911", "Galaxy S23"),
        ("SM-S916", "Galaxy S23+"),
        ("SM-S918", "Galaxy S23 Ultra"),
        // Galaxy S22 family
        ("SM-S901", "Galaxy S22"),
        ("SM-S906", "Galaxy S22+"),
        ("SM-S908", "Galaxy S22 Ultra"),
        // Galaxy S21 / S20 / Note
        ("SM-G991", "Galaxy S21"),
        ("SM-G996", "Galaxy S21+"),
        ("SM-G998", "Galaxy S21 Ultra"),
        ("SM-G981", "Galaxy S20"),
        ("SM-G986", "Galaxy S20+"),
        ("SM-G988", "Galaxy S20 Ultra"),
        ("SM-N98", "Galaxy Note20"),
        ("SM-N97", "Galaxy Note10"),
        ("SM-N96", "Galaxy Note9"),
        ("SM-N95", "Galaxy Note8"),
        // Foldables
        ("SM-F956", "Galaxy Z Fold6"),
        ("SM-F958", "Galaxy Z Fold7"),
        ("SM-F946", "Galaxy Z Fold5"),
        ("SM-F936", "Galaxy Z Fold4"),
        ("SM-F926", "Galaxy Z Fold3"),
        // Tablets
        ("SM-X9", "Galaxy Tab S series"),
        ("SM-X8", "Galaxy Tab S series"),
        ("SM-T9", "Galaxy Tab S series"),
        // Older S series with DeX
        ("SM-G97", "Galaxy S10"),
        ("SM-G96", "Galaxy S9"),
        ("SM-G95", "Galaxy S8"),
    ];

    /// <summary>True when the USB vendor id belongs to Samsung.</summary>
    public static bool IsSamsungVendor(int vendorId) => vendorId == SamsungVendorId;

    /// <summary>
    /// Looks up DeX support from the <c>ro.product.model</c> value.
    /// </summary>
    public static DexSupportInfo Lookup(string? model, string? manufacturer = null)
    {
        bool samsung = manufacturer is null
            || manufacturer.Contains("samsung", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(model))
        {
            return new DexSupportInfo(
                samsung ? DexSupportLevel.Unknown : DexSupportLevel.Unsupported,
                null,
                "The device did not report a model code.");
        }

        string normalized = model.Trim().ToUpperInvariant();

        foreach ((string prefix, string name) in KnownModels)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new DexSupportInfo(DexSupportLevel.Supported, name, null);
            }
        }

        if (samsung || normalized.StartsWith("SM-", StringComparison.Ordinal))
        {
            return new DexSupportInfo(
                DexSupportLevel.Unknown,
                model,
                "This Samsung model is not in the catalog. DeX streaming will be attempted anyway; " +
                "display capture works on any Android 10+ device even where DeX is unavailable.");
        }

        return new DexSupportInfo(
            DexSupportLevel.Unsupported,
            model,
            "DeX is a Samsung feature. Display mirroring may still work on this device.");
    }
}
