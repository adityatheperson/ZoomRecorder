namespace ZoomRecorder.App.Renaming;

internal static class RecordingRenameErrorMessages
{
    internal const string Unavailable =
        "The recording could not be renamed. Close any app using its file and try again.";

    internal static string For(RecordingRenameErrorCode code) => code switch
    {
        RecordingRenameErrorCode.InvalidName => "Enter a valid file name.",
        RecordingRenameErrorCode.NameInUse => "A recording with that name already exists.",
        RecordingRenameErrorCode.ProcessingActive =>
            "Wait for processing to finish before renaming this recording.",
        _ => Unavailable
    };
}
