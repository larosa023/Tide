using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Tide.Core;

/// <summary>
/// Blocks apps with ordinary Windows Firewall rules (one inbound and one outbound per program),
/// grouped under "Tide" so they're easy to find in wf.msc and survive Tide being closed or removed.
/// </summary>
public sealed class FirewallManager
{
    public const string RuleGroup = "Tide";
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;

    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event Action? Changed;

    public string? LastError { get; private set; }

    public IReadOnlyCollection<string> BlockedPaths
    {
        get { lock (_gate) return new List<string>(_blocked); }
    }

    public bool IsBlocked(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        lock (_gate) return _blocked.Contains(path);
    }

    private static dynamic Policy()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new InvalidOperationException("Windows Firewall is not available on this system.");
        return Activator.CreateInstance(t)!;
    }

    private static string RuleName(string path, bool outbound) =>
        $"Tide – Block {(outbound ? "outbound" : "inbound")}: {path}";

    /// <summary>Reads Tide's existing rules from Windows Firewall.</summary>
    public void Refresh()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic policy = Policy();
            foreach (dynamic rule in policy.Rules)
            {
                string? group = rule.Grouping;
                if (group != RuleGroup) continue;
                string? app = rule.ApplicationName;
                int action = rule.Action;
                if (!string.IsNullOrEmpty(app) && action == NET_FW_ACTION_BLOCK)
                    found.Add(app);
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("Firewall refresh: " + ex);
        }

        lock (_gate)
        {
            _blocked.Clear();
            _blocked.UnionWith(found);
        }
        Changed?.Invoke();
    }

    public bool Block(string path)
    {
        try
        {
            dynamic policy = Policy();
            RemoveRules(policy, path); // avoid duplicates
            AddRule(policy, path, outbound: true);
            AddRule(policy, path, outbound: false);
            lock (_gate) _blocked.Add(path);
            LastError = null;
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("Firewall block: " + ex);
            return false;
        }
    }

    public bool Unblock(string path)
    {
        try
        {
            dynamic policy = Policy();
            RemoveRules(policy, path);
            lock (_gate) _blocked.Remove(path);
            LastError = null;
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("Firewall unblock: " + ex);
            return false;
        }
    }

    private static void AddRule(dynamic policy, string path, bool outbound)
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FWRule")!;
        dynamic rule = Activator.CreateInstance(t)!;
        rule.Name = RuleName(path, outbound);
        rule.Description = "Created by Tide. Unblock the app in Tide, or delete this rule, to restore internet access.";
        rule.ApplicationName = path;
        rule.Direction = outbound ? NET_FW_RULE_DIR_OUT : NET_FW_RULE_DIR_IN;
        rule.Action = NET_FW_ACTION_BLOCK;
        rule.Profiles = NET_FW_PROFILE2_ALL;
        rule.Grouping = RuleGroup;
        rule.Enabled = true;
        policy.Rules.Add(rule);
    }

    private static void RemoveRules(dynamic policy, string path)
    {
        // Rules.Remove deletes one rule per call, so keep going until none are left.
        foreach (var outbound in new[] { true, false })
        {
            var name = RuleName(path, outbound);
            for (int i = 0; i < 8 && RuleExists(policy, name); i++)
                policy.Rules.Remove(name);
        }

        // Also clear any Tide-group rule for this program that has a different name (e.g. renamed by the user).
        var stray = new List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            string? group = rule.Grouping;
            string? app = rule.ApplicationName;
            if (group == RuleGroup && string.Equals(app, path, StringComparison.OrdinalIgnoreCase))
                stray.Add((string)rule.Name);
        }
        foreach (var n in stray)
            policy.Rules.Remove(n);
    }

    private static bool RuleExists(dynamic policy, string name)
    {
        try
        {
            var r = policy.Rules.Item(name);
            return r != null;
        }
        catch
        {
            return false;
        }
    }
}
