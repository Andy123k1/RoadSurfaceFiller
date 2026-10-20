using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Colossal.Serialization.Entities;
using Game.City;
using Game.Settings;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    // A city owns its complete editable library, independently of the globally
    // applied snapshot and of copies offered to other cities.
    public struct SavedCityStylingLibrary : IComponentData, IEmptySerializable { }
    public struct CityStylingLibraryJson : IBufferElementData, ISerializable
    {
        public FixedString4096Bytes Text;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(Text.ToString());
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        { reader.Read(out string text); Text = new FixedString4096Bytes(text); }
    }

    internal sealed class StylingProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Default";
        public List<SurfaceRule> GeneralRules { get; set; } = new List<SurfaceRule>();
        public List<SegmentUpgradePreset> SegmentRules { get; set; } = new List<SegmentUpgradePreset>();
    }
    internal sealed class CityStylingLibrary
    {
        public int Version { get; set; } = 1;
        public string CityId { get; set; } = Guid.NewGuid().ToString("N");
        public string CityName { get; set; } = "This city";
        public string ActiveProfileId { get; set; }
        public bool LegacySharedLibraryMigrationDone { get; set; }
        public List<StylingProfile> Profiles { get; set; } = new List<StylingProfile>();
    }
    internal sealed class ArchivedStylingProfile
    {
        public string CityId { get; set; } = "";
        public string CityName { get; set; } = "";
        public StylingProfile Profile { get; set; }
    }

    internal static partial class RuntimeState
    {
        internal static string StylingProfilesMessage = "Waiting for this city's styling profiles.";
    }

    public sealed partial class Setting
    {
        [SettingsUIHidden]
        public string StylingProfileArchiveJson { get; set; } = "";
        private bool m_CityStylingExpected;
        private Action m_SaveCityStyling;
        private string m_ArchiveFingerprint;
        private List<ArchivedStylingProfile> m_Archive;
        private bool m_ArchiveReadFailed;
        internal CityStylingLibrary CityStylingLibrary { get; private set; }
        internal StylingProfile ActiveStylingProfile => CityStylingLibrary?.Profiles.Find(profile => profile.Id == CityStylingLibrary.ActiveProfileId);
        internal bool CityStylingReady => ActiveStylingProfile != null;

        internal void DeactivateCityStyling()
        { m_CityStylingExpected = true; CityStylingLibrary = null; m_SaveCityStyling = null; }

        internal void ActivateCityStyling(CityStylingLibrary library, Action save)
        { CityStylingLibrary = library; m_CityStylingExpected = true; m_SaveCityStyling = save; }

        private List<ArchivedStylingProfile> ProfileArchive()
        {
            string json = StylingProfileArchiveJson ?? "";
            if (m_Archive != null && m_ArchiveFingerprint == json) return m_Archive;
            m_ArchiveFingerprint = json; m_ArchiveReadFailed = false;
            try
            {
                m_Archive = string.IsNullOrWhiteSpace(json) ? new List<ArchivedStylingProfile>() :
                    JsonConvert.DeserializeObject<List<ArchivedStylingProfile>>(json) ?? new List<ArchivedStylingProfile>();
                if (m_Archive.Any(item => item?.Profile == null || string.IsNullOrEmpty(item.Profile.Id) ||
                    item.Profile.GeneralRules == null || item.Profile.SegmentRules == null ||
                    item.Profile.GeneralRules.Any(rule => rule?.Assignments == null) ||
                    item.Profile.SegmentRules.Any(preset => preset?.Rule?.Assignments == null)))
                    throw new JsonSerializationException("A shared profile copy is unreadable.");
            }
            catch (JsonException exception)
            {
                m_Archive = new List<ArchivedStylingProfile>(); m_ArchiveReadFailed = true;
                Mod.Log.Error("Cannot read shared styling profile copies; the saved archive is retained: " + exception.Message);
            }
            return m_Archive;
        }

        internal void InitializeDefaultStylingProfile()
        {
            List<ArchivedStylingProfile> archive = ProfileArchive();
            if (m_ArchiveReadFailed) return;
            ArchivedStylingProfile saved = archive.Find(item => item.Profile.Id == "legacy-shared-library");
            if (saved != null)
            {
                // Reuse the snapshot made by the earlier local builds. Never
                // refresh its rules from whichever city is currently open.
                if (saved.Profile.Name == "Default" && saved.CityName == "Starting setup") return;
                saved.Profile.Name = "Default"; saved.CityName = "Starting setup";
            }
            else
            {
                // Capture once after loading settings, even when the original
                // library was empty. The entry itself is the durable marker.
                archive.Add(new ArchivedStylingProfile { CityName = "Starting setup", Profile = new StylingProfile {
                    Id = "legacy-shared-library", Name = "Default", GeneralRules = CloneStyling(GetLegacyRules()),
                    SegmentRules = CloneStyling(GetLegacySegmentPresets()) } });
            }
            WriteProfileArchive();
        }

        internal StylingProfile DefaultStylingProfile =>
            ProfileArchive().Find(item => item.Profile.Id == "legacy-shared-library")?.Profile;

        private void WriteProfileArchive()
        {
            if (m_ArchiveReadFailed) return;
            StylingProfileArchiveJson = JsonConvert.SerializeObject(m_Archive);
            m_ArchiveFingerprint = StylingProfileArchiveJson;
            ApplyAndSave();
        }

        internal IEnumerable<ArchivedStylingProfile> GetStylingProfileSources()
        {
            // The current save is authoritative, including when an older save
            // is loaded. The archive provides independent copies, never links.
            IEnumerable<ArchivedStylingProfile> current = (CityStylingLibrary?.Profiles ?? new List<StylingProfile>())
                .Select(profile => new ArchivedStylingProfile { CityId = CityStylingLibrary.CityId,
                    CityName = CityStylingLibrary.CityName, Profile = profile });
            HashSet<string> localIds = new HashSet<string>(current.Select(item => item.Profile.Id));
            return current.Concat(ProfileArchive().Where(item => !localIds.Contains(item.Profile.Id))).ToList();
        }

        internal void PersistCityStyling()
        {
            if (!CityStylingReady) throw new ArgumentException("Wait for this city's styling profiles to load.");
            m_SaveCityStyling?.Invoke();
            List<ArchivedStylingProfile> archive = ProfileArchive();
            if (m_ArchiveReadFailed) return;
            archive.RemoveAll(item => item.CityId == CityStylingLibrary.CityId);
            foreach (StylingProfile profile in CityStylingLibrary.Profiles)
                archive.Add(new ArchivedStylingProfile { CityId = CityStylingLibrary.CityId, CityName = CityStylingLibrary.CityName,
                    Profile = CloneStyling(profile) });
            WriteProfileArchive();
        }

        internal static T CloneStyling<T>(T source) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(source));
        internal static string StylingName(string name, string fallback)
        {
            name = (name ?? "").Trim();
            return name.Length == 0 ? fallback : name.Substring(0, Math.Min(120, name.Length));
        }
        internal StylingProfile FindStylingSource(string id) => GetStylingProfileSources().FirstOrDefault(item => item.Profile.Id == id)?.Profile;

        internal void SelectStylingProfile(string id)
        {
            // Selecting a valid replacement must not depend on the old active
            // ID still existing (in particular after deleting that profile).
            if (CityStylingLibrary == null || !CityStylingLibrary.Profiles.Any(profile => profile.Id == id))
                throw new ArgumentException("Choose one of this city's styling profiles.");
            CityStylingLibrary.ActiveProfileId = id; RuntimeState.NewRoadPresetId = "";
            PersistCityStyling();
        }

        internal void CreateStylingProfile(string name, string sourceId)
        {
            if (!CityStylingReady) throw new ArgumentException("Wait for this city's styling profiles to load.");
            StylingProfile source = string.IsNullOrEmpty(sourceId) ? null : FindStylingSource(sourceId);
            if (!string.IsNullOrEmpty(sourceId) && source == null) throw new ArgumentException("That source profile is unavailable.");
            StylingProfile profile = source == null ? new StylingProfile() : CloneStyling(source);
            profile.Id = Guid.NewGuid().ToString("N");
            profile.Name = StylingName(name, source == null ? "New styling profile" : source.Name + " (copy)");
            foreach (SurfaceRule rule in profile.GeneralRules) rule.Id = Guid.NewGuid().ToString("N");
            foreach (SegmentUpgradePreset preset in profile.SegmentRules) preset.Rule.Id = Guid.NewGuid().ToString("N");
            CityStylingLibrary.Profiles.Add(profile);
            RuntimeState.NotifySegmentPresetsChanged();
            SelectStylingProfile(profile.Id);
        }

        internal void RenameStylingProfile(string name)
        {
            if (!CityStylingReady) throw new ArgumentException("Wait for this city's styling profiles to load.");
            ActiveStylingProfile.Name = StylingName(name, ActiveStylingProfile.Name); PersistCityStyling();
        }

        internal void DeleteStylingProfile()
        {
            if (!CityStylingReady || CityStylingLibrary.Profiles.Count <= 1)
                throw new ArgumentException("Keep at least one styling profile in this city.");
            StylingProfile removed = ActiveStylingProfile;
            StylingProfile next = CityStylingLibrary.Profiles.First(profile => profile.Id != removed.Id);
            // Move the selection before removing the old entry. No observable
            // state has a missing active ID, including during persistence.
            CityStylingLibrary.ActiveProfileId = next.Id;
            CityStylingLibrary.Profiles.Remove(removed);
            RuntimeState.NewRoadPresetId = "";
            RuntimeState.NotifySegmentPresetsChanged();
            PersistCityStyling();
        }

        internal void RepairActiveStylingProfile()
        {
            if (CityStylingLibrary == null || CityStylingLibrary.Profiles.Count == 0 || ActiveStylingProfile != null) return;
            CityStylingLibrary.ActiveProfileId = CityStylingLibrary.Profiles[0].Id;
            RuntimeState.NewRoadPresetId = "";
            PersistCityStyling();
            RuntimeState.StylingProfilesMessage = "Recovered the profile selection. Existing road finishes are kept.";
        }

        internal void CopyStylingRule(string sourceId, string targetId, string kind, string ruleId)
        {
            StylingProfile source = FindStylingSource(sourceId);
            StylingProfile target = CityStylingLibrary?.Profiles.Find(profile => profile.Id == targetId);
            if (source == null || target == null) throw new ArgumentException("Choose a source and a destination in this city.");
            if (source.Id == target.Id) throw new ArgumentException("Choose a different destination profile, or use Duplicate.");
            if (kind == "general")
            {
                SurfaceRule original = source.GeneralRules.Find(rule => rule.Id == ruleId);
                if (original == null) throw new ArgumentException("That general rule is unavailable.");
                SurfaceRule copy = CloneStyling(original); copy.Id = Guid.NewGuid().ToString("N"); target.GeneralRules.Add(copy);
            }
            else if (kind == "segment")
            {
                SegmentUpgradePreset original = source.SegmentRules.Find(preset => preset.Rule.Id == ruleId);
                if (original == null) throw new ArgumentException("That segment rule is unavailable.");
                SegmentUpgradePreset copy = CloneStyling(original); copy.Rule.Id = Guid.NewGuid().ToString("N"); target.SegmentRules.Add(copy);
                RuntimeState.NotifySegmentPresetsChanged();
            }
            else throw new ArgumentException("Choose a general rule or a segment rule to copy.");
            PersistCityStyling();
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private Entity m_CityStylingRecord;

        private void ResetCityStyling()
        {
            m_CityStylingRecord = Entity.Null; Mod.ActiveSetting?.DeactivateCityStyling();
            RuntimeState.StylingProfilesMessage = "Waiting for this city's styling profiles.";
        }

        private void InitializeCityStyling(Setting setting, List<SurfaceRule> applied)
        {
            try
            {
                using (NativeArray<Entity> records = GetEntityQuery(ComponentType.ReadOnly<SavedCityStylingLibrary>()).ToEntityArray(Allocator.Temp))
                    if (records.Length > 0) m_CityStylingRecord = records[0];
                CityStylingLibrary library;
                if (m_CityStylingRecord != Entity.Null)
                {
                    if (!EntityManager.HasBuffer<CityStylingLibraryJson>(m_CityStylingRecord))
                        throw new ArgumentException("The saved styling library has no data. Its record has been retained.");
                    StringBuilder json = new StringBuilder();
                    foreach (CityStylingLibraryJson chunk in EntityManager.GetBuffer<CityStylingLibraryJson>(m_CityStylingRecord, true)) json.Append(chunk.Text.ToString());
                    library = JsonConvert.DeserializeObject<CityStylingLibrary>(json.ToString());
                    if (library == null || library.Version != 1 || library.Profiles == null || library.Profiles.Count == 0)
                        throw new ArgumentException("This saved styling library could not be read. Its record has been retained.");
                    NormalizeStylingLibrary(library);
                    StylingProfile defaults = setting.DefaultStylingProfile;
                    foreach (StylingProfile profile in library.Profiles)
                        if (profile.Name == "City styling" && defaults != null &&
                            ProfileStylingSignature(profile) == ProfileStylingSignature(defaults)) profile.Name = "Default";
                    RuntimeState.StylingProfilesMessage = "Styling profiles loaded from this city's save.";
                }
                else
                {
                    library = CreateInitialStylingLibrary(setting.DefaultStylingProfile, new StylingProfile {
                        GeneralRules = Setting.CloneStyling(applied), SegmentRules = RecoverCitySegmentPresets(setting) });
                    RuntimeState.StylingProfilesMessage = "Default starts with your setup from before the update. Create an empty profile or duplicate Default for this city. Existing road finishes are kept.";
                }
                // Retain the old additive field for compatibility with local
                // saves. Loading a library never imports or appends profiles.
                library.LegacySharedLibraryMigrationDone = true;
                NormalizeStylingLibrary(library);
                library.CityName = Setting.StylingName(World.GetOrCreateSystemManaged<CityConfigurationSystem>().cityName, "This city");
                setting.ActivateCityStyling(library, () => SaveCityStylingLibrary(library));
                setting.PersistCityStyling();
                RuntimeState.NotifySegmentPresetsChanged();
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            {
                setting.DeactivateCityStyling();
                RuntimeState.StylingProfilesMessage = "Could not load this city's styling profiles. Existing road finishes are kept. " + exception.Message;
                Mod.Log.Error(RuntimeState.StylingProfilesMessage);
            }
        }

        private static string GeneralStylingSignature(SurfaceRule rule)
        {
            SurfaceRule copy = Setting.CloneStyling(rule); copy.Id = "";
            return JsonConvert.SerializeObject(copy);
        }

        private static string SegmentStylingSignature(SegmentUpgradePreset preset)
        {
            SegmentUpgradePreset copy = Setting.CloneStyling(preset);
            copy.Rule.Id = ""; copy.UpdateExistingSegments = false;
            return JsonConvert.SerializeObject(copy);
        }

        private static string ProfileStylingSignature(StylingProfile profile) => JsonConvert.SerializeObject(new {
            general = profile.GeneralRules.Select(GeneralStylingSignature).ToArray(),
            segment = profile.SegmentRules.Select(SegmentStylingSignature).ToArray()
        });

        private static CityStylingLibrary CreateInitialStylingLibrary(StylingProfile defaults, StylingProfile recovered)
        {
            StylingProfile profile = Setting.CloneStyling(defaults ?? new StylingProfile());
            profile.Id = Guid.NewGuid().ToString("N"); profile.Name = "Default";
            foreach (SurfaceRule rule in profile.GeneralRules) rule.Id = Guid.NewGuid().ToString("N");
            foreach (SegmentUpgradePreset preset in profile.SegmentRules) preset.Rule.Id = Guid.NewGuid().ToString("N");
            HashSet<SurfaceRule> matched = new HashSet<SurfaceRule>();
            bool keepSavedGeneralRules = false;
            foreach (SurfaceRule rule in recovered.GeneralRules)
            {
                string signature = GeneralStylingSignature(rule);
                SurfaceRule match = profile.GeneralRules.Find(candidate => !matched.Contains(candidate) && GeneralStylingSignature(candidate) == signature);
                if (match == null) keepSavedGeneralRules = true;
                else { matched.Add(match); match.Id = rule.Id; }
            }
            List<SegmentUpgradePreset> savedSegments = new List<SegmentUpgradePreset>();
            foreach (SegmentUpgradePreset preset in recovered.SegmentRules)
            {
                string signature = SegmentStylingSignature(preset);
                SegmentUpgradePreset match = profile.SegmentRules.Find(candidate => !matched.Contains(candidate.Rule) && SegmentStylingSignature(candidate) == signature);
                if (match == null) savedSegments.Add(Setting.CloneStyling(preset));
                else { matched.Add(match.Rule); match.Rule.Id = preset.Rule.Id; }
            }
            CityStylingLibrary library = new CityStylingLibrary { ActiveProfileId = profile.Id,
                LegacySharedLibraryMigrationDone = true, Profiles = new List<StylingProfile> { profile } };
            if (keepSavedGeneralRules || savedSegments.Count > 0)
            {
                // Older cities can contain finishes different from the last
                // shared library. Preserve those definitions and their native
                // segment IDs instead of dropping or relinking their roads.
                library.Profiles.Add(new StylingProfile { Name = "Saved road finishes",
                    GeneralRules = keepSavedGeneralRules ? Setting.CloneStyling(recovered.GeneralRules) : new List<SurfaceRule>(),
                    SegmentRules = savedSegments });
            }
            NormalizeStylingLibrary(library);
            return library;
        }

        private static void NormalizeStylingLibrary(CityStylingLibrary library)
        {
            if (string.IsNullOrWhiteSpace(library.CityId)) library.CityId = Guid.NewGuid().ToString("N");
            HashSet<string> profileIds = new HashSet<string>(), ruleIds = new HashSet<string>();
            foreach (StylingProfile profile in library.Profiles)
            {
                if (profile == null) throw new ArgumentException("A saved styling profile is missing.");
                if (string.IsNullOrEmpty(profile.Id) || !profileIds.Add(profile.Id))
                { profile.Id = Guid.NewGuid().ToString("N"); profileIds.Add(profile.Id); }
                profile.Name = Setting.StylingName(profile.Name, "City styling");
                profile.GeneralRules = profile.GeneralRules ?? new List<SurfaceRule>();
                profile.SegmentRules = profile.SegmentRules ?? new List<SegmentUpgradePreset>();
                foreach (SurfaceRule rule in profile.GeneralRules.Concat(profile.SegmentRules.Select(item => item?.Rule)))
                {
                    if (rule == null) throw new ArgumentException("A saved profile contains an unreadable rule.");
                    if (string.IsNullOrEmpty(rule.Id) || !ruleIds.Add(rule.Id))
                    { rule.Id = Guid.NewGuid().ToString("N"); ruleIds.Add(rule.Id); }
                    rule.Name = Setting.StylingName(rule.Name, "Surface rule"); rule.RoadPrefab = rule.RoadPrefab ?? "";
                    if (rule.Assignments == null) throw new ArgumentException("A saved profile contains an unreadable surface list.");
                    foreach (SurfaceAssignment item in rule.Assignments)
                    {
                        if (item == null) throw new ArgumentException("A saved surface choice is missing.");
                        item.Surface = item.Surface ?? "";
                    }
                }
            }
            if (!library.Profiles.Any(profile => profile.Id == library.ActiveProfileId)) library.ActiveProfileId = library.Profiles[0].Id;
        }

        private List<SegmentUpgradePreset> RecoverCitySegmentPresets(Setting setting)
        {
            List<SegmentUpgradePreset> result = new List<SegmentUpgradePreset>();
            HashSet<string> recovered = new HashSet<string>();
            List<SegmentUpgradePreset> legacy = setting.GetLegacySegmentPresets();
            using (NativeArray<Entity> records = GetEntityQuery(ComponentType.ReadOnly<SavedSegmentSurfaceRule>(),
                ComponentType.ReadOnly<SavedSegmentSurfaceTarget>()).ToEntityArray(Allocator.Temp))
                foreach (Entity record in records)
                {
                    SavedSegmentSurfaceRule saved = EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record);
                    if (!IsRoadEdge(saved.Edge)) continue;
                    string link = SegmentPresetId(record);
                    SegmentUpgradePreset match = legacy.Find(preset => (string.IsNullOrEmpty(link) || preset.Rule.Id == link) && SegmentMatchesPreset(record, saved, preset));
                    if (match != null)
                    {
                        if (!recovered.Add(match.Rule.Id)) continue;
                        SegmentUpgradePreset copy = Setting.CloneStyling(match);
                        copy.UpdateExistingSegments = false; result.Add(copy); continue;
                    }
                    // Detached/independent overrides retain their road records;
                    // recover named linked presets even if the shared definition
                    // was changed in a different city after this city was saved.
                    if (string.IsNullOrEmpty(link) || !recovered.Add(link)) continue;
                    SurfaceRule rule = new SurfaceRule { Id = link, Name = saved.Name.ToString(), Enabled = saved.Enabled,
                        SeparateBikeLanes = saved.BikeMode == 0 ? (bool?)null : saved.BikeMode == 2,
                        SeparateBusLanes = saved.BusMode == 0 ? (bool?)null : saved.BusMode == 2,
                        FillBusShoulders = saved.FillBusShoulders, Assignments = new List<SurfaceAssignment>() };
                    foreach (SavedSegmentSurfaceTarget target in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
                        rule.Assignments.Add(new SurfaceAssignment { Target = target.Target, Surface = target.SurfaceName.ToString(),
                            UseRoadPavement = SegmentUsesRoadPavement(record, target.Target) });
                    result.Add(new SegmentUpgradePreset { Rule = rule, FillBusShoulders = saved.FillBusShoulders });
                }
            return result;
        }

        private void SaveCityStylingLibrary(CityStylingLibrary library)
        {
            library.CityName = Setting.StylingName(World.GetOrCreateSystemManaged<CityConfigurationSystem>().cityName, library.CityName);
            string json = JsonConvert.SerializeObject(library);
            if (m_CityStylingRecord == Entity.Null || !EntityManager.Exists(m_CityStylingRecord))
            { m_CityStylingRecord = EntityManager.CreateEntity(typeof(SavedCityStylingLibrary)); EntityManager.AddBuffer<CityStylingLibraryJson>(m_CityStylingRecord); }
            DynamicBuffer<CityStylingLibraryJson> chunks = EntityManager.GetBuffer<CityStylingLibraryJson>(m_CityStylingRecord);
            chunks.Clear();
            for (int offset = 0; offset < json.Length;)
            {
                int length = Math.Min(800, json.Length - offset);
                if (char.IsHighSurrogate(json[offset + length - 1])) length--;
                chunks.Add(new CityStylingLibraryJson { Text = new FixedString4096Bytes(json.Substring(offset, length)) }); offset += length;
            }
        }
    }
}
