using System.Text.Json;

namespace TaskAssist.Core;

// The adapter reads one COM item. Paging, failure isolation and wire limits are tested without a mailbox.
public sealed record MailReadResult(string EntryId, IncomingMail? Mail = null, bool BeforeStart = false);
public static class MailPager
{
    public static OutlookReply Read(ScanCursor previous, int total, Func<int, MailReadResult> read)
    {
        var c = Copy.Of(previous); var messages = new List<IncomingMail>(); var complete = false;
        var bytes = 32768; // Envelope and bounded failure-position list reserve.
        for (var index = c.Offset + 1; index <= Math.Min(total, previous.Offset + 50); index++)
        {
            MailReadResult item;
            try { item = read(index); }
            catch
            {
                c.Offset = index; c.PreviousEntryId = ""; c.Errors++;
                if (c.FailedPositions.Count < 1000) c.FailedPositions.Add(index);
                continue;
            }
            if (item.Mail is { } mail)
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(mail).Length + 1;
                if (size > PipeProtocol.MaxBytes - 32768)
                {
                    mail = mail with { Subject = "容量超過のため原本確認が必要", Sender = "", Body = "", Attachments = [], InternetId = "", AppJobId = "", Readable = false, ReadError = "capacity" };
                    size = JsonSerializer.SerializeToUtf8Bytes(mail).Length + 1;
                }
                if (messages.Count > 0 && bytes + size > PipeProtocol.MaxBytes - 32768) break;
                messages.Add(mail); bytes += size; c.Saved++;
                if (!mail.Readable) c.Errors++;
            }
            c.Offset = index; c.PreviousEntryId = item.EntryId;
            if (item.BeforeStart) { complete = true; break; }
        }
        c.Remaining = complete ? 0 : Math.Max(0, total - c.Offset);
        return new() { Success = true, Messages = messages, Cursor = c, Complete = complete || c.Offset >= total };
    }
}
