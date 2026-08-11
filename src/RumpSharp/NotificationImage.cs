namespace RumpSharp;

/// <summary>Prepares <see cref="Notification.ImagePath"/> files for delivery to macOS.</summary>
internal static class NotificationImage
{
    /// <summary>Copies an image to a throwaway location and returns the copy's path.</summary>
    /// <param name="imagePath">The caller's image file.</param>
    /// <returns>The path of the copy to hand to macOS.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.</exception>
    /// <remarks>
    /// macOS takes ownership of attachment files and moves them into its own store, so it must never
    /// be given the caller's file - the original would disappear from where the caller put it.
    /// </remarks>
    internal static string CopyForDelivery(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Notification image not found: {imagePath}", imagePath);
        }

        var directory = Path.Combine(Path.GetTempPath(), "rumpsharp-attachments");
        Directory.CreateDirectory(directory);

        var copy = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(imagePath));
        File.Copy(imagePath, copy, overwrite: true);
        return copy;
    }
}
