namespace TarkovHelper.Models
{
    /// <summary>
    /// Type of quest event from game logs
    /// </summary>
    public enum QuestEventType
    {
        /// <summary>
        /// Quest started (message.type == 10)
        /// </summary>
        Started,

        /// <summary>
        /// Quest completed successfully (message.type == 12)
        /// </summary>
        Completed,

        /// <summary>
        /// Quest failed (message.type == 11)
        /// </summary>
        Failed
    }

    /// <summary>
    /// Profile/server family detected from the push-notification websocket hostname.
    /// Seasonal PvP must not be merged into the normal PvP or PvE profile.
    /// </summary>
    public enum LogProfileKind
    {
        Unknown = 0,
        Pvp = 1,
        Pve = 2,
        SeasonalPvp = 3
    }

    /// <summary>
    /// Represents a quest event parsed from game logs
    /// </summary>
    public class QuestLogEvent
    {
        /// <summary>
        /// Quest ID from templateId (first token)
        /// </summary>
        public string QuestId { get; set; } = string.Empty;

        /// <summary>
        /// Type of event (Started, Completed, Failed)
        /// </summary>
        public QuestEventType EventType { get; set; }

        /// <summary>
        /// Trader ID from dialogId
        /// </summary>
        public string TraderId { get; set; } = string.Empty;

        /// <summary>
        /// Event timestamp from dt field (Unix timestamp converted to DateTime)
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Original log line for debugging
        /// </summary>
        public string? OriginalLine { get; set; }

        /// <summary>
        /// Source log file name
        /// </summary>
        public string? SourceFile { get; set; }

        /// <summary>
        /// Profile/server family detected from the source log file.
        /// </summary>
        public LogProfileKind SourceProfile { get; set; } = LogProfileKind.Unknown;

        /// <summary>
        /// EFT PMC profile identifier associated with this character when it can be
        /// resolved from the application log next to the notification log.
        /// </summary>
        public string? CharacterProfileId { get; set; }

        /// <summary>
        /// Internal archive generation. This keeps events from an old wipe from being
        /// replayed into a newly created character with the same profile family.
        /// </summary>
        public string? ArchiveGenerationId { get; set; }
    }

    /// <summary>
    /// Result of quest log synchronization
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Total quest events found in logs
        /// </summary>
        public int TotalEventsFound { get; set; }

        /// <summary>
        /// Events loaded from TarkovHelper's compact persistent archive.
        /// </summary>
        public int ArchivedEventsLoaded { get; set; }

        /// <summary>
        /// New events copied into the persistent archive during this scan.
        /// </summary>
        public int NewEventsArchived { get; set; }

        /// <summary>
        /// Events ignored because they belonged to another profile or an unsupported seasonal profile.
        /// </summary>
        public int SkippedOtherProfileEvents { get; set; }

        /// <summary>
        /// Profile types detected in the scanned log files.
        /// </summary>
        public List<LogProfileKind> DetectedLogProfiles { get; set; } = new();

        /// <summary>
        /// Quests marked as started
        /// </summary>
        public int QuestsStarted { get; set; }

        /// <summary>
        /// Quests marked as completed (from log events)
        /// </summary>
        public int QuestsCompleted { get; set; }

        /// <summary>
        /// Quests marked as failed
        /// </summary>
        public int QuestsFailed { get; set; }

        /// <summary>
        /// Prerequisites auto-completed due to started quests
        /// </summary>
        public int PrerequisitesAutoCompleted { get; set; }

        /// <summary>
        /// Quest IDs that couldn't be matched to TarkovTask
        /// </summary>
        public List<string> UnmatchedQuestIds { get; set; } = new();

        /// <summary>
        /// Errors encountered during sync
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// List of quests that will be completed (for confirmation dialog)
        /// </summary>
        public List<QuestChangeInfo> QuestsToComplete { get; set; } = new();

        /// <summary>
        /// List of quests that are currently in progress (started but not completed/failed)
        /// </summary>
        public List<TarkovTask> InProgressQuests { get; set; } = new();

        /// <summary>
        /// List of quests that were completed (from log events)
        /// </summary>
        public List<TarkovTask> CompletedQuests { get; set; } = new();

        /// <summary>
        /// Groups of alternative quests (mutually exclusive) that need user selection
        /// Each group contains quests where one must be chosen as completed
        /// </summary>
        public List<AlternativeQuestGroup> AlternativeQuestGroups { get; set; } = new();

        /// <summary>
        /// Count of alternative quest groups that need user selection
        /// </summary>
        public int AlternativeQuestCount => AlternativeQuestGroups.Count;

        /// <summary>
        /// Whether the sync was successful overall
        /// </summary>
        public bool Success => Errors.Count == 0 || TotalEventsFound > 0;
    }

    /// <summary>
    /// A group of mutually exclusive quests where user must select one
    /// </summary>
    public class AlternativeQuestGroup
    {
        /// <summary>
        /// List of quests in this group (user selects one)
        /// </summary>
        public List<AlternativeQuestChoice> Choices { get; set; } = new();

        /// <summary>
        /// Whether any quest in this group needs to be selected
        /// (true if at least one is a prerequisite for a started/completed quest)
        /// </summary>
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// A choice within an alternative quest group
    /// </summary>
    public class AlternativeQuestChoice
    {
        /// <summary>
        /// The quest
        /// </summary>
        public TarkovTask Task { get; set; } = null!;

        /// <summary>
        /// Whether this choice is selected
        /// </summary>
        public bool IsSelected { get; set; }

        /// <summary>
        /// Whether this quest is already completed
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Whether this quest is already failed
        /// </summary>
        public bool IsFailed { get; set; }
    }

    /// <summary>
    /// ViewModel for alternative quest group display in sync dialog
    /// </summary>
    public class AlternativeQuestGroupViewModel
    {
        private static int _groupCounter = 0;

        /// <summary>
        /// Unique group name for RadioButton grouping
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// Display label for the group
        /// </summary>
        public string GroupLabel { get; set; } = string.Empty;

        /// <summary>
        /// Choices in this group
        /// </summary>
        public List<AlternativeQuestChoiceViewModel> Choices { get; set; } = new();

        /// <summary>
        /// Original group data
        /// </summary>
        public AlternativeQuestGroup OriginalGroup { get; set; } = null!;

        public AlternativeQuestGroupViewModel()
        {
            GroupName = $"AltQuestGroup_{++_groupCounter}";
        }

        /// <summary>
        /// Reset group counter (call before creating new set of ViewModels)
        /// </summary>
        public static void ResetCounter() => _groupCounter = 0;
    }

    /// <summary>
    /// ViewModel for alternative quest choice display in sync dialog
    /// </summary>
    public class AlternativeQuestChoiceViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        /// <summary>
        /// Quest name for display
        /// </summary>
        public string QuestName { get; set; } = string.Empty;

        /// <summary>
        /// Group name for RadioButton grouping
        /// </summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this choice is selected
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        /// <summary>
        /// Whether this quest is already completed
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Whether this quest is already failed
        /// </summary>
        public bool IsFailed { get; set; }

        /// <summary>
        /// Whether this choice can be selected (not failed)
        /// </summary>
        public bool IsEnabled => !IsFailed;

        /// <summary>
        /// Original choice data
        /// </summary>
        public AlternativeQuestChoice OriginalChoice { get; set; } = null!;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Information about a quest change for the confirmation dialog
    /// </summary>
    public class QuestChangeInfo
    {
        /// <summary>
        /// Quest display name
        /// </summary>
        public string QuestName { get; set; } = string.Empty;

        /// <summary>
        /// Quest normalized name for identification
        /// </summary>
        public string NormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Trader name
        /// </summary>
        public string Trader { get; set; } = string.Empty;

        /// <summary>
        /// Whether this is a prerequisite auto-completion
        /// </summary>
        public bool IsPrerequisite { get; set; }

        /// <summary>
        /// The change type (Completed, Failed, Started)
        /// </summary>
        public QuestEventType ChangeType { get; set; }

        /// <summary>
        /// Whether this quest is selected for completion
        /// </summary>
        public bool IsSelected { get; set; } = true;

        /// <summary>
        /// Event timestamp for chronological ordering
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Formatted timestamp for display
        /// </summary>
        public string FormattedTimestamp => Timestamp != default
            ? Timestamp.ToString("MM/dd HH:mm")
            : "";
    }
}
