using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>A picture chosen for a profile or a group, ready to upload.</summary>
    public sealed record PickedPicture(byte[] Bytes, string ContentType, int? Width, int? Height);

    public static class PicturePicker
    {
        /// <summary>
        /// A small, upright picture without its metadata, as it is shown to everyone who sees the profile or group;
        /// null with nothing picked, and an error to show when picking didn't work out.
        /// </summary>
        public static async Task<(PickedPicture? Picture, string? Error)> PickAsync(string title)
        {
            FileResult? picked;
            try
            {
                picked = (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
                {
                    Title = title,
                    SelectionLimit = 1,
                    MaximumWidth = 512,
                    MaximumHeight = 512,
                    CompressionQuality = 85,
                    RotateImage = true,
                    PreserveMetaData = false,
                }) ?? []).FirstOrDefault(result => result is not null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return (null, e is PermissionException ? "Нет доступа к фото. Разрешите его в настройках." : "Не удалось открыть фото.");
            }
            if (picked is null)
                return (null, null);

            byte[] bytes;
            try
            {
                await using var stream = await picked.OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (null, "Не удалось прочитать фото.");
            }
            if (bytes.Length > Limits.AvatarMax)
                return (null, $"Фото больше {Limits.AvatarMax / 1024 / 1024} МБ.");

            var contentType = picked.ContentType is "image/png" ? "image/png" : "image/jpeg";
            var size = ImageSize.Of(bytes);
            return (new PickedPicture(bytes, contentType, size?.Width, size?.Height), null);
        }
    }
}
