﻿using Fan.Exceptions;
using Fan.Helpers;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Fan.Medias
{
    /// <summary>
    /// The media service manages media files: resizes image file, generates unique filename, passes file to storage, 
    /// generates handler url.  
    /// </summary>
    /// <remarks>
    /// See Media model class and admin Media.cshtml page for more information.
    /// </remarks>
    public class MediaService : IMediaService
    {
        /// <summary>
        /// Images with a width or height longer than this value will be optimized.
        /// </summary>
        public const int IMAGE_OPTIMIZED_SIZE = 690;
        //public const int IMAGE_OPTIMIZED_SIZE_HEIGHT = 500;

        /// <summary>
        /// If image file size exceeds 5MB then use a lower quality.
        /// </summary>
        public const long IMAGE_MAX_LEN = 5 * ByteSize.BytesInMegaByte;

        /// <summary>
        /// This will prefix the image url to trigger <see cref="Image.cshtml"/>.
        /// </summary>
        public const string IMAGE_HANDLER_PATH = "/image";

        /// <summary>
        /// Max len for a media filename is 128.
        /// </summary>
        public const int MEDIA_FILENAME_MAXLEN = 128;

        /// <summary>
        /// Accepted image types: .jpg .jpeg .png .gif
        /// </summary>
        /// <remarks>
        /// Getting this idea from WP https://en.support.wordpress.com/images/
        /// For accepted file types https://en.support.wordpress.com/accepted-filetypes/
        /// </remarks>
        public static readonly string[] Accepted_Image_Types = { ".jpg", ".jpeg", ".gif", ".png" };

        private readonly IStorageProvider _storageProvider;
        private readonly IMediaRepository _mediaRepo;
        public MediaService(IStorageProvider storageProvider, IMediaRepository mediaRepo)
        {
            _storageProvider = storageProvider;
            _mediaRepo = mediaRepo;
        }

        /// <summary>
        /// Returns image handler url after uploading image byte array to storage. 
        /// </summary>
        /// <returns>
        /// It saves two copies for each uploaded image, original and optimized, it returns the url to the original.
        /// The optimized is only used by Admin Media page.
        /// </returns>
        /// <param name="source"></param>
        /// <param name="appType"></param>
        /// <param name="userId"></param>
        /// <param name="fileNameOrig"></param>
        /// <param name="uploadFrom"></param>
        /// <returns></returns>
        public async Task<string> UploadImageAsync(byte[] source, EAppType appType, int userId, string fileNameOrig, EUploadedFrom uploadFrom)
        {
            // slugged and encoded file names
            var (fileNameSlugged, titleAttri) = ProcessFileName(fileNameOrig, uploadFrom);

            // ingredients
            var appName = appType.ToString().ToLowerInvariant();
            var uploadedOn = DateTimeOffset.UtcNow;
            var year = uploadedOn.Year.ToString();
            var month = uploadedOn.Month.ToString("d2");

            // resize and save
            var (uniqueFileName, width, height, optimized) = await ResizeAndSaveAsync(null, source, appType, userId, uploadedOn, fileNameSlugged);

            // create record in db
            await CreateMediaAsync(userId, appType, uniqueFileName, titleAttri, source.LongLength, uploadedOn, uploadFrom,
                width, height, optimized);

            // an url that will hit Image.cshtml for original
            return $"{IMAGE_HANDLER_PATH}/{appName}/original/{userId}/{year}/{month}/{uniqueFileName}";
        }

        /// <summary>
        /// Returns image handler url after uploading image stream to storage. 
        /// </summary>
        /// <returns>
        /// It saves two copies for each uploaded image, original and optimized, it returns the url to the original.
        /// The optimized is only used by Admin Media page.
        /// </returns>
        public async Task<string> UploadImageAsync(Stream source, EAppType appType, int userId, string fileNameOrig, EUploadedFrom uploadFrom)
        {
            // slugged and encoded file names
            var (fileNameSlugged, titleAttri) = ProcessFileName(fileNameOrig, uploadFrom);

            // ingredients
            var appName = appType.ToString().ToLowerInvariant();
            var uploadedOn = DateTimeOffset.UtcNow;
            var year = uploadedOn.Year.ToString();
            var month = uploadedOn.Month.ToString("d2");

            // resize and save
            var (uniqueFileName, width, height, optimized) = await ResizeAndSaveAsync(source, null, appType, userId, uploadedOn, fileNameSlugged);

            // create record in db
            await CreateMediaAsync(userId, appType, uniqueFileName, titleAttri, source.Length, uploadedOn, uploadFrom,
                width, height, optimized);

            // an url that will hit Image.cshtml for original
            return $"{IMAGE_HANDLER_PATH}/{appName}/original/{userId}/{year}/{month}/{uniqueFileName}";
        }

        public async Task<Media> UpdateMediaAsync(int id, string title, string description)
        {
            var media = await _mediaRepo.GetAsync(id);
            title = title.IsNullOrEmpty() ? "" : title;
            media.Title = title.Length > MEDIA_FILENAME_MAXLEN ?
             title.Substring(0, MEDIA_FILENAME_MAXLEN) : title;
            media.Description = description;

            await _mediaRepo.UpdateAsync(media);
            return media;
        }

        public async Task<List<Media>> GetMediasAsync(EMediaType mediaType, int pageNumber, int pageSize)
        {
            return await _mediaRepo.GetMediasAsync(mediaType, pageNumber, pageSize);
        }
       
        // -------------------------------------------------------------------- private

        /// <summary>
        /// Takes the original filename user is uploading and returns a slugged filename and title attribute.
        /// </summary>
        /// <remarks>
        /// If the filename is too long it shorten it. Then it generates a slugged filename which 
        /// is hyphen separeated value for english original filenames, a random string value for 
        /// non-english filenames.  The title attribute is original filename html-encoded for safe
        /// display.
        /// </remarks>
        /// <param name="fileNameOrig">Original filename user is uploading.</param>
        /// <param name="uploadFrom">This is used solely because of olw quirks I have to handle.</param>
        /// <returns></returns>
        private (string fileNameSlugged, string titleAttri) ProcessFileName(string fileNameOrig, EUploadedFrom uploadFrom)
        {
            // verify ext is supported
            var ext = Path.GetExtension(fileNameOrig);
            if (ext.IsNullOrEmpty() || !Accepted_Image_Types.Contains(ext, StringComparer.InvariantCultureIgnoreCase))
                throw new FanException("Upload file type is not supported.");

            // make sure file name is not too long
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileNameOrig);
            if (fileNameWithoutExt.Length > MEDIA_FILENAME_MAXLEN)
            {
                fileNameWithoutExt = fileNameWithoutExt.Substring(0, MEDIA_FILENAME_MAXLEN);
            }

            // there is a quirk file uploaded from olw had "_2" suffixed to the name
            if (uploadFrom == EUploadedFrom.MetaWeblog && fileNameWithoutExt.EndsWith("_2"))
            {
                fileNameWithoutExt = fileNameWithoutExt.Remove(fileNameWithoutExt.Length - 2);
            }

            // slug file name
            var slug = Util.FormatSlug(fileNameWithoutExt);
            if (slug.IsNullOrEmpty()) // slug may end up empty
            {
                slug = Util.RandomString(6);
            }
            else if (uploadFrom == EUploadedFrom.MetaWeblog && slug == "thumb") // or may end up with only "thumb" for olw
            {
                slug = string.Concat(Util.RandomString(6), "_thumb");
            }

            var fileNameSlugged = $"{slug}{ext}";
            var fileNameEncoded = WebUtility.HtmlEncode(fileNameWithoutExt);

            return (fileNameSlugged: fileNameSlugged, titleAttri: fileNameEncoded);
        }

        /// <summary>
        /// Resizes and saves image to storage.
        /// </summary>
        /// <remarks>
        /// TODO 
        /// 1. based on aspect ratio corp very tall or wide images.
        /// 2. figure out better ways to control quality vs file size; should I dec quality by 5 each mb up.
        /// 3. should I use a height limit.
        /// </remarks>
        /// <param name="source">Image stream, either source or source2 should be available.</param>
        /// <param name="source2">Image byte[]</param>
        /// <param name="appType"></param>
        /// <param name="userId"></param>
        /// <param name="uploadedOn"></param>
        /// <param name="fileNameSlugged"></param>
        /// <returns>
        /// 1. unique filename
        /// 2. original image width
        /// 3. original image height
        /// 4. optimized
        /// </returns>
        private async Task<(string fileNameUnique, int width, int height, bool optimized)> ResizeAndSaveAsync(
            Stream source, byte[] source2, EAppType appType, int userId, DateTimeOffset uploadedOn, string fileNameSlugged)
        {
            var fileNameUnique = "";
            var image = source2 == null ? new MagickImage(source) : new MagickImage(source2);
            var length = source2 == null ? source.Length : source2.LongLength;

            using (image)
            {
                var optimized = false;
                int width = image.Width;
                int height = image.Height;

                // if either image width or height is greater than optimized size, we need to opti
                if (width > IMAGE_OPTIMIZED_SIZE || height > IMAGE_OPTIMIZED_SIZE) // IMAGE_OPTIMIZED_SIZE_HEIGHT
                {
                    optimized = true;
                }

                // original
                using (var memStream = new MemoryStream())
                {
                    var sizeOrig = new MagickGeometry(width, height)
                    {
                        IgnoreAspectRatio = true // since it's resizing to its orig size
                    };

                    image.Quality = length >= IMAGE_MAX_LEN ? 60 : 75; // if too large, use lower quality
                    image.Resize(sizeOrig);
                    image.Write(memStream);
                    memStream.Position = 0;
                    fileNameUnique = await _storageProvider.SaveFileAsync(memStream, appType, userId, uploadedOn, fileNameSlugged, EImageSize.Original);
                }

                // optimized (TODO based on aspect ratio corp very tall or wide images)
                if (optimized)
                {
                    using (var memStream = new MemoryStream())
                    {
                        if (width > height) // horizontal rectangle
                        {
                            width = IMAGE_OPTIMIZED_SIZE;
                            height = Convert.ToInt32(image.Height * IMAGE_OPTIMIZED_SIZE / (double) image.Width);
                        }
                        else // vertial rect or square
                        {
                            width = Convert.ToInt32(image.Width * IMAGE_OPTIMIZED_SIZE / (double) image.Height); // IMAGE_OPTIMIZED_SIZE_HEIGHT
                            height = IMAGE_OPTIMIZED_SIZE; // IMAGE_OPTIMIZED_SIZE_HEIGHT
                        }

                        var sizeOptimize = new MagickGeometry(width, height);
                        image.Quality = 75; // since this is resized from bigger to a smaller size use higher quality 75
                        image.Resize(sizeOptimize);
                        image.Write(memStream);
                        memStream.Position = 0;

                        fileNameUnique = await _storageProvider.SaveFileAsync(memStream, appType, userId, uploadedOn, fileNameSlugged, EImageSize.Optimized);
                    }
                }

                return (fileNameUnique: fileNameUnique, width: image.Width, height: image.Height, optimized: optimized);
            }
        }

        /// <summary>
        /// Saves <see cref="Media"/> record to datasource.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="appType"></param>
        /// <param name="uniqueFileName">unique filename from storage provider</param>
        /// <param name="titleAttri">Original filename used as title, it is html encoded.</param>
        /// <param name="length"></param>
        /// <param name="uploadedOn"></param>
        /// <param name="uploadFrom"></param>
        /// <returns></returns>
        private async Task CreateMediaAsync(int userId, EAppType appType, string uniqueFileName, string titleAttri, 
            long length, DateTimeOffset uploadedOn, EUploadedFrom uploadFrom, int width, int height, bool optimized)
        {
            // get file type, arg exception will throw if the file type is unknow, TODO handle this
            var contentType = MimeTypeMap.GetMimeType(Path.GetExtension(uniqueFileName));
            // if file type is longer than 256 limit, TODO handle that
            var fileType = contentType.Substring(contentType.LastIndexOf("/") + 1).ToLowerInvariant();

            await _mediaRepo.CreateAsync(new Media
            {
                UserId = userId,
                AppType = appType,
                FileName = uniqueFileName,
                Title = titleAttri,
                Description = null,
                Length = length,
                MediaType = EMediaType.Image,
                UploadedOn = uploadedOn,
                UploadedFrom = uploadFrom,
                FileType = fileType,
                Width = width,
                Height = height,
                Optimized = optimized,
            });
        }
    }
}