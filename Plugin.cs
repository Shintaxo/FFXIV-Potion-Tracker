using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PotionTrackerDiagnostic;

// All observations are local, transient and based on the Medicated status, not item-use packets.
public sealed class Plugin : IDalamudPlugin
{
    private const uint Medicated = 49;
    private const string Command = "/potions";
    private const string LegacyCommand = "/ptrdiag";
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PrepotWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlausibilityCooldown = TimeSpan.FromMinutes(4.5);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IPartyList Party { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;

    private readonly WindowSystem windows = new("PotionTrackerDiagnostic");
    private readonly ResultsWindow window;
    private readonly Dictionary<string, MemberObservation> observations = new();
    private Pull? currentPull;
    private Pull? lastPull;
    private DateTimeOffset? lastSample;
    private DateTimeOffset? lastCombatEnd;
    private bool? lastCombat;
    private uint? lastTerritory;
    private long? lastPartyId;
    private string? lastRoster;

    public Plugin()
    {
        this.window = new ResultsWindow(this);
        this.windows.AddWindow(this.window);
        Commands.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Show the latest local pull's party potion observations."
        });
        Commands.AddHandler(LegacyCommand, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Alias for /potions."
        });
        Framework.Update += this.OnUpdate;
        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
    }

    public void Dispose()
    {
        Framework.Update -= this.OnUpdate;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        Commands.RemoveHandler(Command);
        Commands.RemoveHandler(LegacyCommand);
        this.windows.RemoveAllWindows();
    }

    private void OnCommand(string command, string args) => this.window.IsOpen = true;
    private void OpenMainUi() => this.window.IsOpen = true;

    private void OnUpdate(IFramework framework)
    {
        var now = DateTimeOffset.UtcNow;
        var player = Objects.LocalPlayer;
        if (!ClientState.IsLoggedIn || player is null)
        {
            this.InvalidateContinuity();
            this.currentPull = null;
            this.observations.Clear();
            this.lastCombat = null;
            this.lastCombatEnd = null;
            this.lastSample = null;
            this.lastTerritory = null;
            this.lastPartyId = null;
            this.lastRoster = null;
            return;
        }

        var territory = ClientState.TerritoryType;
        var gap = this.lastSample is { } previous && now - previous > MaxSampleGap;
        if (gap || (this.lastTerritory is { } previousTerritory && previousTerritory != territory))
            this.InvalidateContinuity();
        this.lastTerritory = territory;
        this.lastSample = now;

        var members = ReadMembers(player);
        var roster = string.Join("|", members.Select(m => m.Key).OrderBy(key => key));
        if ((this.lastPartyId is { } partyId && partyId != Party.PartyId) ||
            (this.lastRoster is not null && this.lastRoster != roster))
        {
            this.InvalidateContinuity();
        }
        this.lastPartyId = Party.PartyId;
        this.lastRoster = roster;

        var seen = new HashSet<string>();
        foreach (var member in members)
        {
            seen.Add(member.Key);
            if (!this.observations.TryGetValue(member.Key, out var observation))
            {
                observation = new MemberObservation();
                this.observations[member.Key] = observation;
            }

            // A matching, visible battle actor is required. Party-list status data alone is
            // not treated as continuous observation across actor spawn/despawn.
            var actor = member.Actor;
            if (actor is null || !actor.IsValid() || actor.GameObjectId == 0 ||
                (member.EntityId != 0 && actor.EntityId != member.EntityId))
            {
                observation.ResetVisibility();
                this.MarkGap(member.Key);
                continue;
            }
            if (observation.ActorId != 0 && observation.ActorId != actor.GameObjectId)
            {
                observation.ResetVisibility();
                this.MarkGap(member.Key);
            }
            observation.ActorId = actor.GameObjectId;

            var present = actor.StatusList.Any(status => status.StatusId == Medicated);
            if (observation.Present == false && present)
            {
                // Cooldown is only a plausibility/dedup filter. It never invents a use.
                if (observation.LastEvent is null || now - observation.LastEvent >= PlausibilityCooldown)
                {
                    observation.LastEvent = now;
                    observation.Events.Add(now);
                    if (observation.Events.Count > 16)
                        observation.Events.RemoveAt(0);
                    if (this.currentPull is { } activePull &&
                        activePull.Rows.TryGetValue(member.Key, out var row) &&
                        now >= activePull.Start)
                        row.Events.Add(now);
                }
                else
                {
                    // Suppressed implausible onsets must not become a confident "no".
                    this.MarkGap(member.Key);
                }
                observation.CleanAbsentSince = null;
            }
            else if (!present && observation.Present != false)
            {
                observation.CleanAbsentSince = now;
            }
            observation.Present = present;
        }

        foreach (var key in this.observations.Keys.ToArray())
        {
            if (seen.Contains(key))
                continue;
            this.observations[key].ResetVisibility();
            this.MarkGap(key);
        }

        // The local InCombat edge is a pull proxy, not proof of a boss engage or wipe.
        var combat = Conditions[ConditionFlag.InCombat];
        if (this.lastCombat == false && combat)
            this.StartPull(now, members);
        else if (this.lastCombat == true && !combat)
            this.FinishPull(now);
        this.lastCombat = combat;
    }

    private void StartPull(DateTimeOffset now, List<MemberSnapshot> members)
    {
        if (this.currentPull is not null)
        {
            this.InvalidateContinuity();
            this.FinishPull(now);
        }

        var pull = new Pull(now);
        foreach (var member in members)
        {
            this.observations.TryGetValue(member.Key, out var observation);
            var row = new PullRow(member.Name, member.JobId, member.PartyOrder)
            {
                // A negative result requires a clean absent baseline throughout the
                // complete prepot window, then uninterrupted visibility until pull end.
                CompletePrepotCoverage = observation?.CleanAbsentSince is { } clean &&
                                         clean <= now - PrepotWindow
            };
            if (observation is not null)
            {
                foreach (var occurrence in observation.Events)
                    if (occurrence >= now - PrepotWindow &&
                        occurrence <= now &&
                        (this.lastCombatEnd is null || occurrence > this.lastCombatEnd))
                        row.Events.Add(occurrence);
            }
            pull.Rows[member.Key] = row;
        }
        this.currentPull = pull;
    }

    private void FinishPull(DateTimeOffset now)
    {
        var pull = this.currentPull;
        if (pull is null)
            return;
        pull.End = now;
        foreach (var row in pull.Rows.Values)
            row.Completed = true;
        this.lastPull = pull;
        this.currentPull = null;
        this.lastCombatEnd = now;
        var count = pull.Rows.Values.Count(row => row.Events.Count > 0);
        Chat.Print($"{count}/{pull.PartySize} players used potions. /potions for details");
    }

    private void MarkGap(string key)
    {
        if (this.currentPull is { } activePull &&
            activePull.Rows.TryGetValue(key, out var row))
            row.Gap = true;
    }

    private void InvalidateContinuity()
    {
        foreach (var observation in this.observations.Values)
            observation.ResetVisibility();
        if (this.currentPull is not null)
            foreach (var row in this.currentPull.Rows.Values)
                row.Gap = true;
    }

    private static List<MemberSnapshot> ReadMembers(IBattleChara player)
    {
        var members = new List<MemberSnapshot>();
        if (Party.Length == 0)
        {
            members.Add(LocalMember(player, 0));
            return members;
        }

        var partyMembers = new IPartyMember?[Party.Length];
        for (var i = 0; i < Party.Length; i++)
            partyMembers[i] = Party[i];

        // Prefer persistent content ID, then current actor identity. GameObject may
        // be null even for the local player, so it cannot be the only match.
        var localContentId = PlayerState.IsLoaded ? PlayerState.ContentId : 0;
        var localIndex = Array.FindIndex(partyMembers, member =>
            member is not null && localContentId != 0 && member.ContentId == localContentId);
        if (localIndex < 0 && player.EntityId != 0)
            localIndex = Array.FindIndex(partyMembers, member =>
                member is not null && member.EntityId == player.EntityId);
        if (localIndex < 0 && player.GameObjectId != 0)
            localIndex = Array.FindIndex(partyMembers, member =>
                member?.GameObject?.GameObjectId == player.GameObjectId);
        if (localIndex < 0 && PlayerState.IsLoaded && PlayerState.HomeWorld.RowId != 0)
        {
            // Name + home world is unique for player characters. Use it only when
            // the roster itself has exactly one match, never name alone.
            var matches = Enumerable.Range(0, partyMembers.Length)
                .Where(i => partyMembers[i] is { } member &&
                    member.World.RowId == PlayerState.HomeWorld.RowId &&
                    string.Equals(member.Name.ToString(), player.Name.ToString(), StringComparison.Ordinal))
                .Take(2).ToArray();
            if (matches.Length == 1)
                localIndex = matches[0];
        }

        for (var i = 0; i < partyMembers.Length; i++)
        {
            var member = partyMembers[i];
            // A null slot cannot identify a person and may itself be the local
            // player. Do not add a placeholder that can duplicate the fallback.
            if (member is null)
                continue;
            if (i == localIndex)
            {
                members.Add(LocalMember(player, i));
                continue;
            }
            var gameObject = member.GameObject;
            var key = member.ContentId != 0
                ? $"C{member.ContentId}"
                : member.EntityId != 0 ? $"E{member.EntityId}"
                : gameObject?.GameObjectId is { } objectId && objectId != 0 ? $"G{objectId}"
                : $"U{i}";
            var actor = gameObject as IBattleChara;
            if (actor is null || (member.EntityId != 0 && actor.EntityId != member.EntityId))
                actor = member.EntityId != 0 ? Objects.SearchByEntityId(member.EntityId) as IBattleChara : null;
            members.Add(new MemberSnapshot(key, member.Name.ToString(),
                member.ClassJob.RowId, member.EntityId, actor, i));
        }
        if (localIndex < 0)
            members.Add(LocalMember(player, partyMembers.Length));
        return members;
    }

    private static MemberSnapshot LocalMember(IBattleChara player, int partyOrder) =>
        new("L", player.Name.ToString(), player.ClassJob.RowId, player.EntityId, player, partyOrder);

    private static Role JobRole(uint jobId) => jobId switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => Role.Tank,
        6 or 24 or 28 or 33 or 40 => Role.Healer,
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 or 43 => Role.Melee,
        5 or 23 or 31 or 38 => Role.PhysRange,
        7 or 25 or 26 or 27 or 35 or 36 or 42 => Role.Caster,
        _ => Role.Unknown
    };

    private enum Role { Tank, Healer, Melee, PhysRange, Caster, Unknown }

    private static string Timing(DateTimeOffset at, DateTimeOffset start)
    {
        var offset = at - start;
        var magnitude = offset.Duration();
        var time = magnitude.TotalHours >= 1
            ? $"{(int)magnitude.TotalHours}:{magnitude.Minutes:00}:{magnitude.Seconds:00}"
            : $"{(int)magnitude.TotalMinutes}:{magnitude.Seconds:00}";
        return offset < TimeSpan.Zero ? "Prepull" : time;
    }

    private sealed record MemberSnapshot(string Key, string Name, uint JobId,
        uint EntityId, IBattleChara? Actor, int PartyOrder);

    private sealed class MemberObservation
    {
        public bool? Present { get; set; }
        public ulong ActorId { get; set; }
        public DateTimeOffset? CleanAbsentSince { get; set; }
        public DateTimeOffset? LastEvent { get; set; }
        public List<DateTimeOffset> Events { get; } = new();

        public void ResetVisibility()
        {
            this.Present = null;
            this.ActorId = 0;
            this.CleanAbsentSince = null;
        }
    }

    private sealed class Pull
    {
        public Pull(DateTimeOffset start)
        {
            this.Start = start;
        }
        public DateTimeOffset Start { get; }
        public DateTimeOffset? End { get; set; }
        public int PartySize => this.Rows.Count;
        public Dictionary<string, PullRow> Rows { get; } = new();
    }

    private sealed class PullRow
    {
        public PullRow(string name, uint jobId, int partyOrder)
        {
            this.Name = name;
            this.JobId = jobId;
            this.PartyOrder = partyOrder;
        }
        public string Name { get; }
        public uint JobId { get; }
        public int PartyOrder { get; }
        public bool CompletePrepotCoverage { get; set; }
        public bool Gap { get; set; }
        public bool Completed { get; set; }
        public List<DateTimeOffset> Events { get; } = new();
        public string Verdict => this.Events.Count > 0 ? "✓" :
            this.Completed && this.CompletePrepotCoverage && !this.Gap ? "✗" : "?";
        public string Amount => this.Events.Count == 0
            ? this.Verdict == "✗" ? "0" : "?"
            : this.Events.Count.ToString();
    }

    private sealed class ResultsWindow : Window
    {
        private readonly Plugin plugin;

        public ResultsWindow(Plugin plugin) : base("Potion Tracker")
        {
            this.plugin = plugin;
            this.Size = new Vector2(750, 380);
            this.SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void Draw()
        {
            var pull = this.plugin.currentPull ?? this.plugin.lastPull;
            if (pull is null)
            {
                ImGui.TextUnformatted("No local pull observed yet.");
                return;
            }
            ImGui.TextUnformatted($"Pull: {pull.Start:HH:mm:ss} UTC  |  Party: {pull.PartySize}  |  {(pull.End is null ? "ongoing" : "ended")}");
            ImGui.TextUnformatted("✓ observed  |  ✗ none  |  ? uncertain");
            if (!ImGui.BeginTable("potions", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                return;
            ImGui.TableSetupColumn("Class/Job");
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Potion");
            ImGui.TableSetupColumn("Amount");
            ImGui.TableSetupColumn("Timing");
            ImGui.TableHeadersRow();
            foreach (var row in pull.Rows.Values.OrderBy(row => JobRole(row.JobId))
                         .ThenBy(row => row.PartyOrder))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (JobRole(row.JobId) != Role.Unknown)
                {
                    // Framed ClassJob icon family (e.g. PLD 62119, VPR 62141).
                    var icon = Textures.GetFromGameIcon(new GameIconLookup { IconId = 62100u + row.JobId });
                    if (icon.TryGetWrap(out var wrap, out _))
                        ImGui.Image(wrap.Handle, new Vector2(22, 22));
                    else
                        ImGui.TextUnformatted("?");
                }
                else
                    ImGui.TextUnformatted("?");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Verdict);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Amount);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Events.Count == 0
                    ? "—"
                    : string.Join(", ", row.Events.Select(at => Timing(at, pull.Start))));
            }
            ImGui.EndTable();
        }
    }
}
