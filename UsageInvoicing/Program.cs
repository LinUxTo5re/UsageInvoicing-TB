using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageInvoicing;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var inputPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "usage-data.json");
            if (!File.Exists(inputPath))
            {
                // Fallback: try working directory's usage-data.json
                var wdPath = Path.Combine(Directory.GetCurrentDirectory(), "usage-data.json");
                inputPath = File.Exists(wdPath) ? wdPath : inputPath;
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var loader = new InputLoader();
            var entries = loader.Load(inputPath);

            var calculator = new InvoiceCalculator();
            var printer = new InvoicePrinter();

            foreach (var entry in entries.Valid)
            {
                var invoice = calculator.Calculate(entry);
                printer.Print(invoice, entry);
            }

            foreach (var error in entries.Errors)
            {
                Console.WriteLine($"Skipped invalid entry: {error}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }
}

public record UsageRecord(string CustomerId, int ApiCalls, decimal StorageGb, int ComputeMinutes);

public static class PricingConstants
{
    public const int ApiTierThreshold = 10_000;
    public static readonly decimal ApiRateTier1 = 0.01m; // up to threshold
    public static readonly decimal ApiRateTier2 = 0.008m; // above threshold
    public static readonly decimal StorageRatePerGb = 0.25m;
    public static readonly decimal ComputeRatePerMinute = 0.05m;
}

public class InputLoader
{
    public (List<UsageRecord> Valid, List<string> Errors) Load(string path)
    {
        var valid = new List<UsageRecord>();
        var errors = new List<string>();

        if (!File.Exists(path))
        {
            errors.Add($"Input file not found at '{path}'");
            return (valid, errors);
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            errors.Add($"Unable to read file: {ex.Message}");
            return (valid, errors);
        }

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
            if (array is null)
            {
                errors.Add("Root JSON is not an array");
                return (valid, errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
            return (valid, errors);
        }

        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                errors.Add("Entry is not an object");
                continue;
            }

            string idDisplay = "UNKNOWN";
            try
            {
                // CustomerId
                if (!obj.TryGetPropertyValue("CustomerId", out var idNode) || idNode is null || idNode.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                {
                    throw new InvalidOperationException("Missing CustomerId");
                }
                var id = idNode.ToString();
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Empty CustomerId");
                idDisplay = id;

                // API_Calls
                if (!TryReadInt(obj, "API_Calls", out int apiCalls))
                    throw new InvalidOperationException("Invalid API_Calls");

                // Storage_GB
                if (!TryReadDecimal(obj, "Storage_GB", out decimal storageGb))
                    throw new InvalidOperationException("Invalid Storage_GB");

                // Compute_Minutes
                if (!TryReadInt(obj, "Compute_Minutes", out int computeMinutes))
                    throw new InvalidOperationException("Invalid Compute_Minutes");

                valid.Add(new UsageRecord(id, apiCalls, storageGb, computeMinutes));
            }
            catch (Exception ex)
            {
                errors.Add($"Missing or invalid fields for CustomerId: {idDisplay} ({ex.Message})");
            }
        }

        return (valid, errors);
    }

    private static bool TryReadInt(JsonObject obj, string prop, out int value)
    {
        value = default;
        if (!obj.TryGetPropertyValue(prop, out var node) || node is null)
            return false;
        try
        {
            if (node is JsonValue v)
            {
                var kind = v.GetValueKind();
                if (kind == JsonValueKind.Number)
                {
                    if (v.TryGetValue<int>(out var i)) { value = i; return true; }
                    if (v.TryGetValue<long>(out var l)) { value = checked((int)l); return true; }
                    if (v.TryGetValue<decimal>(out var d)) { value = checked((int)d); return true; }
                    if (v.TryGetValue<double>(out var dbl)) { value = checked((int)dbl); return true; }
                }
                else if (kind == JsonValueKind.String)
                {
                    var s = v.ToString();
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
                    if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) { value = checked((int)d); return true; }
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryReadDecimal(JsonObject obj, string prop, out decimal value)
    {
        value = default;
        if (!obj.TryGetPropertyValue(prop, out var node) || node is null)
            return false;
        try
        {
            if (node is JsonValue v)
            {
                var kind = v.GetValueKind();
                if (kind == JsonValueKind.Number)
                {
                    if (v.TryGetValue<decimal>(out var d)) { value = d; return true; }
                    if (v.TryGetValue<double>(out var dbl)) { value = (decimal)dbl; return true; }
                }
                else if (kind == JsonValueKind.String)
                {
                    var s = v.ToString();
                    if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }
}

public record Invoice(string CustomerId, decimal ApiCost, decimal StorageCost, decimal ComputeCost)
{
    public decimal Total => ApiCost + StorageCost + ComputeCost;
}

public class InvoiceCalculator
{
    public Invoice Calculate(UsageRecord record)
    {
        var apiTier1 = Math.Min(record.ApiCalls, PricingConstants.ApiTierThreshold);
        var apiTier2 = Math.Max(record.ApiCalls - PricingConstants.ApiTierThreshold, 0);

        decimal apiCost = apiTier1 * PricingConstants.ApiRateTier1 + apiTier2 * PricingConstants.ApiRateTier2;
        decimal storageCost = record.StorageGb * PricingConstants.StorageRatePerGb;
        decimal computeCost = record.ComputeMinutes * PricingConstants.ComputeRatePerMinute;

        return new Invoice(record.CustomerId, apiCost, storageCost, computeCost);
    }
}

public class InvoicePrinter
{
    public void Print(Invoice invoice, UsageRecord usage)
    {
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"Invoice for Customer: {invoice.CustomerId}");
        Console.WriteLine(new string('-', 29));
        Console.WriteLine($"API Calls: {usage.ApiCalls} calls -> ${invoice.ApiCost.ToString("F2", ci)}");
        Console.WriteLine($"Storage: {usage.StorageGb.ToString(ci)} GB -> ${invoice.StorageCost.ToString("F2", ci)}");
        Console.WriteLine($"Compute Time: {usage.ComputeMinutes} minutes -> ${invoice.ComputeCost.ToString("F2", ci)}");
        Console.WriteLine(new string('-', 29));
        Console.WriteLine($"Total Due: ${invoice.Total.ToString("F2", ci)}\n");
    }
}
