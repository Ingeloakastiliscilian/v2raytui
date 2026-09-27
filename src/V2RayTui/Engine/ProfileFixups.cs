using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ServiceLib.Helper;

namespace V2RayTui.Engine;

/// <summary>
/// Repairs server parameters that some subscriptions publish in a form the cores reject
/// (the same links fail in v2rayN GUI too). Runs after subscription updates, imports and at start.
/// </summary>
public static partial class ProfileFixups
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    // "2.0", "0.0", "1e3" — a number with a fraction or exponent part.
    [GeneratedRegex(@"[:\[,]\s*-?\d+(\.\d+|[eE][+-]?\d+)")]
    private static partial Regex FloatLike();

    /// <summary>
    /// XHTTP "extra": xray wants integers or "a-b" ranges (xmux.maxConnections etc.) and rejects the
    /// whole config for "2.0" ("Invalid integer range") — in a speed test, the whole batch of servers.
    /// Integral floats are rewritten as integers.
    /// </summary>
    public static async Task<int> NormalizeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var fixedItems = new List<ProfileItem>();
            foreach (var p in await AppManager.Instance.ProfileItems(null) ?? [])
            {
                if (p.Network != nameof(ETransport.xhttp))
                {
                    continue;
                }
                var te = p.GetTransportExtra();
                if (te.XhttpExtra.IsNullOrEmpty() || !FloatLike().IsMatch(te.XhttpExtra))
                {
                    continue;
                }
                if (JsonUtils.ParseJson(te.XhttpExtra) is not { } node || !IntegerizeNumbers(node))
                {
                    continue;
                }
                p.SetTransportExtra(te with { XhttpExtra = node.ToJsonString() });
                fixedItems.Add(p);
            }
            if (fixedItems.Count > 0)
            {
                await SQLiteHelper.Instance.UpdateAllAsync(fixedItems);
                LogBus.WriteFileOnly($"[fixup] XHTTP extra: integral numbers normalized in {fixedItems.Count} server(s): "
                    + string.Join(", ", fixedItems.Take(5).Select(p => p.Remarks)));
            }
            return fixedItems.Count;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ProfileFixups", ex);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IntegerizeNumbers(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is { } child && Replace(child, v => o[key] = v))
                    {
                        changed = true;
                    }
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var idx = i;
                    if (a[i] is { } child && Replace(child, v => a[idx] = v))
                    {
                        changed = true;
                    }
                }
                break;
        }
        return changed;
    }

    private static bool Replace(JsonNode child, Action<JsonNode> set)
    {
        if (child is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var raw = v.ToJsonString();
            if ((raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                && d == Math.Floor(d) && Math.Abs(d) < long.MaxValue)
            {
                set(JsonValue.Create((long)d));
                return true;
            }
            return false;
        }
        return IntegerizeNumbers(child);
    }
}
