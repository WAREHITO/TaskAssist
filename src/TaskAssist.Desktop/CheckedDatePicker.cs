using System.Windows.Controls;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

// WPF can revert invalid text to its old valid date. Keep that rejection separate
// from SelectedDate so an unchanged old value cannot masquerade as a valid edit.
public sealed class CheckedDatePicker : DatePicker
{
    public string? RejectedInput { get; private set; }
    public event Action? InputValidationChanged;
    public CheckedDatePicker()
    {
        DateValidationError += (_,e) =>
        {
            e.ThrowException=false; RejectedInput=e.Text; InputValidationChanged?.Invoke();
        };
        SelectedDateChanged += (_,_) => { RejectedInput=null; InputValidationChanged?.Invoke(); };
    }
    public void ClearRejectedInput()
    {
        SelectedDate=null; Text=""; RejectedInput=null; InputValidationChanged?.Invoke(); Focus();
    }
    public DateOnly? ReadValue()
    {
        if(RejectedInput is not null) throw new RuleException("日付の入力エラーが残っています。「日付を入力し直す」から正しい日を選んでください。他の入力は残しています。");
        return SelectedDate is {} day ? DateOnly.FromDateTime(day) : null;
    }
}
