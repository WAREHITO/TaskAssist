using System.Security.Cryptography;
using System.Text;

namespace TaskAssist.Core;

public sealed record MailFolder(string Account, string StoreId, string EntryId, string Name, bool Special = false)
{
    public string Key => MailIdentity.Hash(Account + "\n" + StoreId + "\n" + EntryId);
    public override string ToString() => Name + (Special ? "（個別確認が必要）" : "");
}
public sealed record MailAccount(string Address, string Name, string StoreId, bool AdditionalStore = false)
{
    public override string ToString() => AdditionalStore ? Name + "（追加・共有領域／個別確認）" : Name + " <" + Address + ">";
}
public sealed record MailLocation(string Account, string StoreId, string FolderId, string EntryId)
{
    public string Key => MailIdentity.Hash(Account + "\n" + StoreId + "\n" + EntryId);
}
public sealed record MailAttachment(int Index, string Name, long Bytes);
public sealed record IncomingMail
{
    public MailLocation Location { get; init; } = new("", "", "", "");
    public string InternetId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
    public bool Readable { get; init; }
    public string ReadError { get; init; } = "";
    public List<MailAttachment> Attachments { get; init; } = [];
    public string AppJobId { get; init; } = "";
    public string Fingerprint => MailIdentity.Hash(Sender + "\n" + ReceivedAt.ToUniversalTime().ToString("O") + "\n" + Subject + "\n" + Body);
}
public static class MailIdentity
{
    public static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
public sealed record ScanCursor
{
    public int Offset { get; set; }
    public string PreviousEntryId { get; set; } = "";
    public DateTimeOffset? CompletedAt { get; set; }
    public int Errors { get; set; }
    public List<int> FailedPositions { get; set; } = [];
    public int Saved { get; set; }
    public int Remaining { get; set; }
}
public sealed record ConnectorSettings
{
    public int Generation { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public List<MailFolder> Folders { get; set; } = [];
    public Dictionary<string, ScanCursor> Cursors { get; set; } = [];
    public string Health { get; set; } = "Outlook未接続。対象を選んで有効にしてください。";
    public DateTimeOffset? LastAttempt { get; set; }
}
public enum MailRuleAction { CreateTask, Ignore }
public sealed record MailRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string FolderKey { get; set; } = "";
    public string Sender { get; set; } = "";
    public string SubjectPrefix { get; set; } = "";
    public string ExactBodyTemplate { get; set; } = "";
    public MailRuleAction Action { get; set; }
    public string NextAction { get; set; } = "内容を確認する";
    public string Completion { get; set; } = "対応を終える";
    public bool Enabled { get; set; }
    public bool Urgent { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}
public enum RecurrenceCadence { Daily, Weekly, Monthly }
public enum CatchUpChoice { Ask, EachOccurrence, LatestOnly }
public sealed record RecurrenceRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Completion { get; set; } = "対応を終える";
    public RecurrenceCadence Cadence { get; set; }
    public CatchUpChoice CatchUp { get; set; }
    public DateOnly StartOn { get; set; }
    public DateOnly? Through { get; set; }
    public bool Enabled { get; set; }
    public bool SkipNonWorkdays { get; set; }
    public int MonthDay { get; set; } = 1;
}
public sealed record RecurrenceOccurrence(string RuleId, DateOnly Day, string? TaskId, string Reason);
public enum JobKind { Attachment, SelfDigest, OutlookDraft }
public enum JobState { Proposed, Authorized, Running, ConfirmedSuccess, ConfirmedFailure, OutcomeUnknown, Suspended, Cancelled }
public sealed record ExternalJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProfileId { get; init; } = "";
    public int RestoreGeneration { get; init; }
    public JobKind Kind { get; init; }
    public JobState State { get; set; }
    public string UniqueKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string SourceId { get; init; } = "";
    public int AttachmentIndex { get; init; }
    public string AttachmentName { get; init; } = "";
    public long AttachmentBytes { get; init; }
    public DateTimeOffset? ExpectedModifiedAt { get; init; }
    public string Destination { get; init; } = "";
    public string Account { get; init; } = "";
    public string Recipient { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string ResultCode { get; set; } = "";
    public string ResultHash { get; set; } = "";
    public string DigestStateHash { get; init; } = "";
}
public sealed record AutomationState
{
    public string Consultation { get; set; } = "";
    public int RestoreGeneration { get; set; }
    public ConnectorSettings Connector { get; set; } = new();
    public List<MailRule> Rules { get; set; } = [];
    public List<MailRule> RuleHistory { get; set; } = [];
    public List<RecurrenceRule> Recurrences { get; set; } = [];
    public List<RecurrenceRule> RecurrenceHistory { get; set; } = [];
    public List<RecurrenceOccurrence> Occurrences { get; set; } = [];
    public List<ExternalJob> Jobs { get; set; } = [];
    public List<DayOfWeek> Workdays { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public List<DateOnly> DaysOff { get; set; } = [];
    public bool WorkCalendarConfirmed { get; set; }
    public bool DigestEnabled { get; set; }
    public string DigestAccount { get; set; } = "";
    public string DigestRecipient { get; set; } = "";
    public TimeOnly DigestTime { get; set; } = new(16, 0);
    public string LastDigestHash { get; set; } = "";
    public bool BackupsEnabled { get; set; }
    public int BackupGenerations { get; set; } = 7;
    public DateOnly? LastBackupDay { get; set; }
    public int CapacityMinutes { get; set; } = 420;
    public bool NotificationsPaused { get; set; }
}

// Fixed protocol: no script, executable, or arbitrary function request is accepted.
public sealed record OutlookRequest
{
    public int Protocol { get; init; } = 1;
    public string Command { get; init; } = "probe";
    public MailAccount? Account { get; init; }
    public MailFolder? Folder { get; init; }
    public DateTimeOffset StartAt { get; init; }
    public ScanCursor Cursor { get; init; } = new();
    public ExternalJob? Job { get; init; }
    public MailLocation? Location { get; init; }
}
public sealed record OutlookReply
{
    public int Protocol { get; init; } = 1;
    public bool Success { get; init; }
    public string Code { get; init; } = "";
    public string Version { get; init; } = "";
    public List<MailAccount> Accounts { get; init; } = [];
    public List<MailFolder> Folders { get; init; } = [];
    public List<IncomingMail> Messages { get; init; } = [];
    public ScanCursor Cursor { get; init; } = new();
    public bool Complete { get; init; }
    public string ResultHash { get; init; } = "";
}
