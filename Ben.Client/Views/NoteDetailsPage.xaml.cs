namespace Ben.Views;

using Ben.Models;
using Ben.Services;
using Ben.ViewModels;
#if IOS
using Foundation;
using CoreGraphics;
using UIKit;
#endif

#nullable enable

public partial class NoteDetailsPage : ContentPage
{
    private readonly DailyViewModel _viewModel;
    private readonly NoteItem _note;
    private readonly bool _isNewNote;
    private bool _isSaving;
    private Thickness _baseLayoutPadding;
    private bool _hasBaseLayoutPadding;

#if IOS
    private NSObject? _keyboardWillShowObserver;
    private NSObject? _keyboardWillHideObserver;
    private NSObject? _keyboardWillChangeFrameObserver;
#endif

    public NoteDetailsPage(DailyViewModel viewModel)
        : this(viewModel, note: null)
    {
    }

    public NoteDetailsPage(DailyViewModel viewModel, NoteItem? note)
    {
        InitializeComponent();
        _viewModel = viewModel;
        CaptureBaseLayoutPadding();

        if (note == null)
        {
            _isNewNote = true;
            _note = new NoteItem
            {
                Key = viewModel.CurrentDay?.Key ?? KeyConvention.ToDateKey(DateTime.Today)
            };
        }
        else
        {
            _isNewNote = false;
            _note = note;
        }

        NoteEditor.Text = _note.Text;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

#if IOS
        SubscribeKeyboardNotifications();
#endif

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            NoteEditor.Focus();
            int length = NoteEditor.Text?.Length ?? 0;
            NoteEditor.CursorPosition = length;
            NoteEditor.SelectionLength = 0;
        });
    }

    protected override void OnDisappearing()
    {
#if IOS
        UnsubscribeKeyboardNotifications();
#endif

        ResetKeyboardInset();
        base.OnDisappearing();
    }

    async void OnSaveClicked(object sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        _isSaving = true;
        if (sender is Button saveButton)
        {
            saveButton.IsEnabled = false;
        }

        try
        {
            string text = NormalizeText(NoteEditor.Text);
            if (string.IsNullOrEmpty(text))
            {
                await DisplayAlertAsync("Validation", "Please enter note text.", "OK");
                return;
            }

            string originalText = _note.Text;
            try
            {
                await _viewModel.SaveNoteDetailsLocallyAsync(_note, text, _isNewNote);
            }
            catch
            {
                _note.Text = originalText;
                throw;
            }

            await Navigation.PopModalAsync();

            _ = _viewModel.CompleteNoteSaveAfterCloseAsync(_note, _isNewNote);
        }
        catch
        {
            await DisplayAlertAsync("Save failed", "Could not save the note. Please try again.", "OK");
        }
        finally
        {
            _isSaving = false;
            if (sender is Button saveButtonFinal)
            {
                saveButtonFinal.IsEnabled = true;
            }
        }
    }

    static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\u00A0", " ")
            .Replace("\u200B", " ")
            .Replace("\uFEFF", " ")
            .Trim();
    }

    async void OnCancelClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    void CaptureBaseLayoutPadding()
    {
        if (_hasBaseLayoutPadding)
        {
            return;
        }

        _baseLayoutPadding = LayoutGrid.Padding;
        _hasBaseLayoutPadding = true;
    }

    void ResetKeyboardInset()
    {
        CaptureBaseLayoutPadding();
        LayoutGrid.Padding = _baseLayoutPadding;
    }

    void ApplyKeyboardBottomInset(double bottomInset)
    {
        CaptureBaseLayoutPadding();
        LayoutGrid.Padding = new Thickness(
            _baseLayoutPadding.Left,
            _baseLayoutPadding.Top,
            _baseLayoutPadding.Right,
            _baseLayoutPadding.Bottom + Math.Max(0, bottomInset));
    }

#if IOS
    void SubscribeKeyboardNotifications()
    {
        if (_keyboardWillShowObserver != null)
        {
            return;
        }

        _keyboardWillShowObserver = UIKeyboard.Notifications.ObserveWillShow(OnKeyboardFrameChanged);
        _keyboardWillChangeFrameObserver = UIKeyboard.Notifications.ObserveWillChangeFrame(OnKeyboardFrameChanged);
        _keyboardWillHideObserver = UIKeyboard.Notifications.ObserveWillHide((_, __) => ResetKeyboardInset());
    }

    void UnsubscribeKeyboardNotifications()
    {
        _keyboardWillShowObserver?.Dispose();
        _keyboardWillShowObserver = null;

        _keyboardWillChangeFrameObserver?.Dispose();
        _keyboardWillChangeFrameObserver = null;

        _keyboardWillHideObserver?.Dispose();
        _keyboardWillHideObserver = null;
    }

    void OnKeyboardFrameChanged(object? sender, UIKeyboardEventArgs args)
    {
        double overlap = GetKeyboardOverlap(args.FrameEnd);
        ApplyKeyboardBottomInset(overlap);
    }

    double GetKeyboardOverlap(CGRect keyboardFrameInScreen)
    {
        if (Handler?.PlatformView is not UIViewController controller || controller.View?.Window == null)
        {
            return keyboardFrameInScreen.Height;
        }

        CGRect keyboardFrameInView = controller.View.ConvertRectFromView(keyboardFrameInScreen, null);
        nfloat overlap = controller.View.Bounds.Bottom - keyboardFrameInView.Top;
        overlap = NMath.Max(0, overlap - controller.View.SafeAreaInsets.Bottom);
        return overlap;
    }
#endif
}
