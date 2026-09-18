using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TaskAssist.Core;

namespace TaskAssist.OutlookWorker;

internal static class Program
{
    [DllImport("oleaut32.dll", PreserveSig = false)] private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith("TaskAssist-", StringComparison.Ordinal) || !int.TryParse(args[1], out var parentId)) return 2;
        try
        {
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parentId) return 3;
            var request = PipeProtocol.ReadAsync<OutlookRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult();
            // COM calls run on this STA with a Windows message loop. Only our worker may be timed out by the parent.
            using var dispatcher = new Control(); dispatcher.CreateControl();
            var context = new ApplicationContext(); OutlookReply? result = null;
            dispatcher.BeginInvoke(() => { try { result = Execute(request); } catch { result = new() { Code = request.Job is null ? "unavailable" : "outcome_unknown" }; } finally { context.ExitThread(); } });
            Application.Run(context);
            PipeProtocol.WriteAsync(pipe, result!, CancellationToken.None).GetAwaiter().GetResult(); return 0;
        }
        catch { return 4; } // No account, message, path, or exception detail is written to stdout/stderr.
    }
    private static T Use<T>(object obj, Func<dynamic,T> action) { try { return action(obj); } finally { if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); } }
    private static OutlookReply Execute(OutlookRequest request)
    {
        if (request.Protocol != 1 || request.Command is not ("probe" or "accounts" or "folders" or "page" or "job" or "open")) return new() { Code = "protocol" };
        using var key = Registry.ClassesRoot.OpenSubKey(@"Outlook.Application\CLSID", false);
        if (!Guid.TryParse(key?.GetValue(null) as string, out var clsid)) return new() { Code = "classic_required" };
        GetActiveObject(ref clsid, IntPtr.Zero, out var app);
        return Use(app, outlook =>
        {
            if (request.Command == "probe") return new OutlookReply { Success = true, Version = (string)outlook.Version, Code = "connected" };
            return Use<OutlookReply>((object)outlook.Session, session =>
            {
                if (request.Command == "accounts") return Accounts(session);
                if (request.Command == "folders") return Folders(session, request.Account ?? throw new InvalidOperationException());
                if (request.Command == "page") return Page(session, request);
                if (request.Command == "open")
                {
                    var location = request.Location ?? throw new InvalidOperationException();
                    return Use<OutlookReply>((object)session.GetItemFromID(location.EntryId, location.StoreId), item => { item.Display(false); return new() { Success = true, Code = "opened" }; });
                }
                return Job(outlook, session, request);
            });
        });
    }
    private static OutlookReply Accounts(dynamic session) => Use((object)session.Accounts, accounts =>
    {
        var result = new List<MailAccount>(); var count = (int)accounts.Count;
        if (count > 50) throw new InvalidOperationException();
        for (var i = 1; i <= count; i++) Use((object)accounts.Item(i), account =>
        {
            var address = (string)account.SmtpAddress;
            if (AutomationPolicy.SingleAddress(address)) Use((object)account.DeliveryStore, store => { result.Add(new(address, (string)account.DisplayName, (string)store.StoreID)); return true; });
            return true;
        });
        Use((object)session.Stores, stores =>
        {
            if ((int)stores.Count > 100) throw new InvalidOperationException();
            for (var i = 1; i <= (int)stores.Count; i++) Use((object)stores.Item(i), store =>
            {
                var id = (string)store.StoreID;
                if (!result.Any(a => a.StoreId == id)) result.Add(new("追加領域:" + MailIdentity.Hash(id),(string)store.DisplayName,id,true));
                return true;
            }); return true;
        });
        return new OutlookReply { Success = true, Accounts = result };
    });
    private static OutlookReply Folders(dynamic session, MailAccount account)
    {
        var accounts = ((OutlookReply)Accounts(session)).Accounts;
        if (!accounts.Any(a => a.Address == account.Address && a.StoreId == account.StoreId)) return new() { Code = "account_changed" };
        return Use((object)session.GetStoreFromID(account.StoreId), store =>
        {
            var special = new HashSet<string>();
            foreach (var kind in new[] { 3,4,5,16,23 }) try { Use((object)store.GetDefaultFolder(kind), f => { special.Add((string)f.EntryID); return true; }); } catch { }
            var folders = new List<MailFolder>();
            void Visit(dynamic folder, string prefix, int depth, bool excluded)
            {
                if (depth > 20 || folders.Count > 1000) throw new InvalidOperationException();
                var name = prefix + (string)folder.Name; var id = (string)folder.EntryID; excluded |= special.Contains(id);
                if ((int)folder.DefaultItemType == 0) folders.Add(new(account.Address, account.StoreId, id, name, excluded));
                Use((object)folder.Folders, children => { for (var n = 1; n <= (int)children.Count; n++) Use((object)children.Item(n), child => { Visit(child, name + " / ", depth + 1, excluded); return true; }); return true; });
            }
            Use((object)store.GetRootFolder(), root => { Visit(root, "", 0, account.AdditionalStore); return true; });
            return new OutlookReply { Success = true, Folders = folders };
        });
    }
    private static OutlookReply Page(dynamic session, OutlookRequest request)
    {
        var scope = request.Folder ?? throw new InvalidOperationException();
        var c = Copy.Of(request.Cursor);
        if (c.Offset < 0 || request.StartAt == default) throw new InvalidOperationException();
        if (!((OutlookReply)Accounts(session)).Accounts.Any(a => a.Address == scope.Account && a.StoreId == scope.StoreId)) return new() { Code = "account_changed" };
        return Use((object)session.GetFolderFromID(scope.EntryId, scope.StoreId), folder => Use((object)folder.Items, items =>
        {
            items.Sort("[ReceivedTime]", true);
            var total = (int)items.Count;
            if (c.Offset > total) return new OutlookReply { Code = "changed" };
            if (c.Offset > 0 && c.PreviousEntryId.Length > 0)
                try { if (Use((object)items.Item(c.Offset), item => (string)item.EntryID) != c.PreviousEntryId) return new OutlookReply { Code = "changed" }; }
                catch { return new OutlookReply { Code = "changed" }; }
            return MailPager.Read(c,total,index =>
                Use<MailReadResult>((object)items.Item(index), item =>
                {
                    var entryId = (string)item.EntryID;
                    if ((int)item.Class != 43) return new(entryId);
                    var received = new DateTimeOffset(DateTime.SpecifyKind((DateTime)item.ReceivedTime, DateTimeKind.Local)).ToUniversalTime();
                    if (received < request.StartAt) return new(entryId,BeforeStart:true);
                    var input = new IncomingMail { Location = new(scope.Account, scope.StoreId, scope.EntryId, entryId), ReceivedAt = received };
                    try
                    {
                        var subject = (string)item.Subject ?? ""; var sender = (string)item.SenderEmailAddress ?? "";
                        if ((string)item.SenderEmailType == "EX")
                            try { sender = Use((object)item.Sender, ae => Use((object)ae.GetExchangeUser(), user => (string)user.PrimarySmtpAddress)); } catch { }
                        input = input with { Subject = subject, Sender = sender };
                        var body = (string)item.Body ?? "";
                        if (body.Length > 64000) throw new InvalidOperationException();
                        var internetId = ""; try { internetId = Use((object)item.PropertyAccessor, p => (string)p.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x1035001F")); } catch { }
                        var jobId = ""; try { jobId = Use((object)item.PropertyAccessor, p => (string)p.GetProperty("http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/TaskAssistJobId")); } catch { }
                        var attachments = Use((object)item.Attachments, collection =>
                        {
                            var result = new List<MailAttachment>();
                            if ((int)collection.Count > 100) throw new InvalidOperationException();
                            for (var k = 1; k <= (int)collection.Count; k++) { var n = k; Use((object)collection.Item(n), a => { result.Add(new(n,(string)a.FileName,(int)a.Size)); return true; }); }
                            return result;
                        });
                        input = input with { Body = body, InternetId = internetId, Attachments = attachments, Readable = true, AppJobId = jobId,
                            LastModifiedAt = new DateTimeOffset(DateTime.SpecifyKind((DateTime)item.LastModificationTime,DateTimeKind.Local)).ToUniversalTime() };
                    }
                    catch { input = input with { Readable = false, ReadError = "body_unavailable" }; }
                    return new(entryId,input);
                }));
        }));
    }
    private static OutlookReply Job(dynamic outlook, dynamic session, OutlookRequest request)
    {
        var j = request.Job ?? throw new InvalidOperationException();
        if (j.State != JobState.Running || !Guid.TryParseExact(j.Id,"N",out _) || !Guid.TryParseExact(j.ProfileId,"N",out _)) throw new InvalidOperationException();
        if (j.Kind == JobKind.Attachment)
        {
            var location = request.Location ?? throw new InvalidOperationException(); var parent = Path.GetDirectoryName(j.Destination)!;
            SafeFiles.ValidateDirectory(parent);
            if (!Path.GetFileName(j.Destination).StartsWith(j.Id + "-",StringComparison.Ordinal) || File.Exists(j.Destination)) throw new InvalidOperationException();
            var temporary = Path.Combine(parent, ".taskassist-" + j.Id + ".tmp");
            if (File.Exists(temporary)) return new() { Code = "outcome_unknown" };
            return Use((object)session.GetItemFromID(location.EntryId, location.StoreId), item => Use((object)item.Attachments, attachments =>
                Use((object)attachments.Item(j.AttachmentIndex), attachment =>
                {
                    var modified = new DateTimeOffset(DateTime.SpecifyKind((DateTime)item.LastModificationTime,DateTimeKind.Local)).ToUniversalTime();
                    if (!SafeFiles.ApprovedAttachmentMatches(j,(string)attachment.FileName,(long)attachment.Size,modified))
                        return new OutlookReply { Code = "attachment_changed" };
                    attachment.SaveAsFile(temporary);
                    using var stream = File.OpenRead(temporary); var hash = Convert.ToHexString(SHA256.HashData(stream)); stream.Close();
                    SafeFiles.WriteReceipt(j,hash);
                    File.Move(temporary,j.Destination,false);
                    return new OutlookReply { Success = true, Code = "file_saved", ResultHash = hash };
                })));
        }
        if (j.Kind is not (JobKind.SelfDigest or JobKind.OutlookDraft) || !AutomationPolicy.SingleAddress(j.Account)) throw new InvalidOperationException();
        if (j.Kind == JobKind.SelfDigest && (!AutomationPolicy.SingleAddress(j.Recipient) || !string.Equals(j.Account,j.Recipient,StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException();
        return Use((object)session.Accounts, accounts =>
        {
            for (var i=1; i <= (int)accounts.Count; i++)
            {
                var result = Use<OutlookReply?>((object)accounts.Item(i), account =>
                {
                    if (!string.Equals((string)account.SmtpAddress,j.Account,StringComparison.OrdinalIgnoreCase)) return null;
                    return Use<OutlookReply>((object)outlook.CreateItem(0), mail =>
                    {
                        mail.SendUsingAccount = account; mail.Subject = j.Subject; mail.Body = j.Body;
                        Use((object)mail.PropertyAccessor, p => { p.SetProperty("http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/TaskAssistJobId",j.Id); return true; });
                        if (j.Kind == JobKind.SelfDigest) { mail.To = j.Recipient; mail.Send(); return new() { Success = true, Code = "send_accepted_not_delivery" }; }
                        mail.Save(); return new() { Success = true, Code = "draft_saved" };
                    });
                });
                if (result is not null) return result;
            }
            return new OutlookReply { Code = "account_changed" };
        });
    }
}
