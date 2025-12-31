#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.XbmcMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.XbmcMetadata.Savers
{
    public abstract partial class BaseNfoSaver : IMetadataFileSaver
    {
        public const string DateAddedFormat = "yyyy-MM-dd HH:mm:ss";

        public const string YouTubeWatchUrl = "https://www.youtube.com/watch?v=";

        private static readonly HashSet<string> _commonTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plot",
            "customrating",
            "lockdata",
            "dateadded",
            "title",
            "rating",
            "year",
            "sorttitle",
            "mpaa",
            "aspectratio",
            "collectionnumber",
            "tmdbid",
            "rottentomatoesid",
            "language",
            "tvcomid",
            "tagline",
            "studio",
            "genre",
            "tag",
            "runtime",
            "actor",
            "criticrating",
            "fileinfo",
            "director",
            "writer",
            "trailer",
            "premiered",
            "releasedate",
            "outline",
            "id",
            "credits",
            "originaltitle",
            "watched",
            "playcount",
            "lastplayed",
            "art",
            "resume",
            "biography",
            "formed",
            "review",
            "style",
            "imdbid",
            "imdb_id",
            "country",
            "audiodbalbumid",
            "audiodbartistid",
            "enddate",
            "lockedfields",
            "zap2itid",
            "tvrageid",

            "musicbrainzartistid",
            "musicbrainzalbumartistid",
            "musicbrainzalbumid",
            "musicbrainzreleasegroupid",
            "tvdbid",
            "collectionitem",

            "isuserfavorite",
            "userrating",

            "countrycode"
        };

        protected BaseNfoSaver(
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILogger<BaseNfoSaver> logger)
        {
            Logger = logger;
            UserDataManager = userDataManager;
            UserManager = userManager;
            LibraryManager = libraryManager;
            ConfigurationManager = configurationManager;
            FileSystem = fileSystem;
        }

        protected IFileSystem FileSystem { get; }

        protected IServerConfigurationManager ConfigurationManager { get; }

        protected ILibraryManager LibraryManager { get; }

        protected IUserManager UserManager { get; }

        protected IUserDataManager UserDataManager { get; }

        protected ILogger<BaseNfoSaver> Logger { get; }

        protected ItemUpdateType MinimumUpdateType
        {
            get
            {
                if (ConfigurationManager.GetNfoConfiguration().SaveImagePathsInNfo)
                {
                    return ItemUpdateType.ImageUpdate;
                }

                return ItemUpdateType.MetadataDownload;
            }
        }

        /// <inheritdoc />
        public string Name => SaverName;

        public static string SaverName => "Nfo";

        // filters control characters but allows only properly-formed surrogate sequences
        // http://web.archive.org/web/20181230211547/https://emby.media/community/index.php?/topic/49071-nfo-not-generated-on-actualize-or-rescan-or-identify
        // Web Archive version of link since it's not really explained in the thread.
        [GeneratedRegex(@"(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F\uFEFF\uFFFE\uFFFF]")]
        private static partial Regex InvalidXMLCharsRegexRegex();

        /// <inheritdoc />
        public string GetSavePath(BaseItem item)
            => GetLocalSavePath(item);

        /// <summary>
        /// Gets the save path.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns><see cref="string" />.</returns>
        protected abstract string GetLocalSavePath(BaseItem item);

        /// <summary>
        /// Gets the name of the root element.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns><see cref="string" />.</returns>
        protected abstract string GetRootElementName(BaseItem item);

        /// <inheritdoc />
        public abstract bool IsEnabledFor(BaseItem item, ItemUpdateType updateType);

        protected virtual IEnumerable<string> GetTagsUsed(BaseItem item)
        {
            foreach (var providerKey in item.ProviderIds.Keys)
            {
                var providerIdTagName = GetTagForProviderKey(providerKey);
                if (!_commonTags.Contains(providerIdTagName))
                {
                    yield return providerIdTagName;
                }
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var path = GetSavePath(item);

            using (var memoryStream = new MemoryStream())
            {
                await SaveAsync(item, memoryStream, path, cancellationToken).ConfigureAwait(false);

                memoryStream.Position = 0;

                cancellationToken.ThrowIfCancellationRequested();

                await SaveToFileAsync(memoryStream, path).ConfigureAwait(false);
            }
        }

        private async Task SaveToFileAsync(Stream stream, string path)
        {
            var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException($"Provided path ({path}) is not valid.", nameof(path));
            Directory.CreateDirectory(directory);

            // On Windows, saving the file will fail if the file is hidden or readonly
            FileSystem.SetAttributes(path, false, false);

            var fileStreamOptions = new FileStreamOptions()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                PreallocationSize = stream.Length,
                Options = FileOptions.Asynchronous
            };

            var filestream = new FileStream(path, fileStreamOptions);
            await using (filestream.ConfigureAwait(false))
            {
                await stream.CopyToAsync(filestream).ConfigureAwait(false);
            }

            if (ConfigurationManager.Configuration.SaveMetadataHidden)
            {
                SetHidden(path, true);
            }
        }

        private void SetHidden(string path, bool hidden)
        {
            try
            {
                FileSystem.SetHidden(path, hidden);
            }
            catch (IOException ex)
            {
                Logger.LogError(ex, "Error setting hidden attribute on {Path}", path);
            }
        }

        private async Task SaveAsync(BaseItem item, Stream stream, string xmlPath, CancellationToken token)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8,
                CloseOutput = false
            };

            using (var writer = XmlWriter.Create(stream, settings))
            {
                var root = GetRootElementName(item);

                await writer.WriteStartDocumentAsync(true).ConfigureAwait(false);
                await writer.WriteStartElementAsync(null, root, null).ConfigureAwait(false);

                var baseItem = item;

                if (baseItem is not null)
                {
                    await AddCommonNodesAsync(baseItem, writer, LibraryManager, UserManager, UserDataManager, ConfigurationManager, token).ConfigureAwait(false);
                }

                WriteCustomElements(item, writer);

                if (baseItem is IHasMediaSources hasMediaSources)
                {
                    AddMediaInfo(hasMediaSources, writer);
                }

                var tagsUsed = GetTagsUsed(item).ToList();

                try
                {
                    AddCustomTags(xmlPath, tagsUsed, writer, Logger);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                catch (XmlException ex)
                {
                    Logger.LogError(ex, "Error reading existing nfo");
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            }
        }

        protected abstract void WriteCustomElements(BaseItem item, XmlWriter writer);

        public static void AddMediaInfo<T>(T item, XmlWriter writer)
            where T : IHasMediaSources
        {
            writer.WriteStartElement("fileinfo");
            writer.WriteStartElement("streamdetails");

            var mediaStreams = item.GetMediaStreams();

            foreach (var stream in mediaStreams)
            {
                writer.WriteStartElement(stream.Type.ToString().ToLowerInvariant());

                if (!string.IsNullOrEmpty(stream.Codec))
                {
                    var codec = stream.Codec;

                    if ((stream.CodecTag ?? string.Empty).Contains("xvid", StringComparison.OrdinalIgnoreCase))
                    {
                        codec = "xvid";
                    }
                    else if ((stream.CodecTag ?? string.Empty).Contains("divx", StringComparison.OrdinalIgnoreCase))
                    {
                        codec = "divx";
                    }

                    writer.WriteElementString("codec", codec);
                    writer.WriteElementString("micodec", codec);
                }

                if (stream.BitRate.HasValue)
                {
                    writer.WriteElementString("bitrate", stream.BitRate.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (stream.Width.HasValue)
                {
                    writer.WriteElementString("width", stream.Width.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (stream.Height.HasValue)
                {
                    writer.WriteElementString("height", stream.Height.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrEmpty(stream.AspectRatio))
                {
                    writer.WriteElementString("aspect", stream.AspectRatio);
                    writer.WriteElementString("aspectratio", stream.AspectRatio);
                }

                var framerate = stream.ReferenceFrameRate;

                if (framerate.HasValue)
                {
                    writer.WriteElementString("framerate", framerate.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrEmpty(stream.Language))
                {
                    writer.WriteElementString("language", InvalidXMLCharsRegexRegex().Replace(stream.Language, string.Empty));
                }

                var scanType = stream.IsInterlaced ? "interlaced" : "progressive";
                writer.WriteElementString("scantype", scanType);

                if (stream.Channels.HasValue)
                {
                    writer.WriteElementString("channels", stream.Channels.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (stream.SampleRate.HasValue)
                {
                    writer.WriteElementString("samplingrate", stream.SampleRate.Value.ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteElementString("default", stream.IsDefault.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("forced", stream.IsForced.ToString(CultureInfo.InvariantCulture));

                if (stream.Type == MediaStreamType.Video)
                {
                    var runtimeTicks = item.RunTimeTicks;
                    if (runtimeTicks.HasValue)
                    {
                        var timespan = TimeSpan.FromTicks(runtimeTicks.Value);

                        writer.WriteElementString(
                            "duration",
                            Math.Floor(timespan.TotalMinutes).ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString(
                            "durationinseconds",
                            Math.Floor(timespan.TotalSeconds).ToString(CultureInfo.InvariantCulture));
                    }

                    if (item is Video video)
                    {
                        // AddChapters(video, builder, itemRepository);

                        if (video.Video3DFormat.HasValue)
                        {
                            switch (video.Video3DFormat.Value)
                            {
                                case Video3DFormat.FullSideBySide:
                                    writer.WriteElementString("format3d", "FSBS");
                                    break;
                                case Video3DFormat.FullTopAndBottom:
                                    writer.WriteElementString("format3d", "FTAB");
                                    break;
                                case Video3DFormat.HalfSideBySide:
                                    writer.WriteElementString("format3d", "HSBS");
                                    break;
                                case Video3DFormat.HalfTopAndBottom:
                                    writer.WriteElementString("format3d", "HTAB");
                                    break;
                                case Video3DFormat.MVC:
                                    writer.WriteElementString("format3d", "MVC");
                                    break;
                            }
                        }
                    }
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        /// <summary>
        /// Adds the common nodes.
        /// </summary>
        private async Task AddCommonNodesAsync(
            BaseItem item,
            XmlWriter writer,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataRepo,
            IServerConfigurationManager config,
            CancellationToken token)
        {
            var writtenProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var overview = (item.Overview ?? string.Empty)
                .StripHtml()
                .Replace("&quot;", "'", StringComparison.Ordinal);

            var options = config.GetNfoConfiguration();

            if (item is MusicArtist)
            {
                await writer.WriteElementStringAsync(null, "biography", null, overview).ConfigureAwait(false);
            }
            else if (item is MusicAlbum)
            {
                await writer.WriteElementStringAsync(null, "review", null, overview).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteElementStringAsync(null, "plot", null, overview).ConfigureAwait(false);
            }

            if (item is not Video)
            {
                await writer.WriteElementStringAsync(null, "outline", null, overview).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(item.CustomRating))
            {
                await writer.WriteElementStringAsync(null, "customrating", null, item.CustomRating).ConfigureAwait(false);
            }

            await writer.WriteElementStringAsync(null, "lockdata", null, item.IsLocked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()).ConfigureAwait(false);

            if (item.LockedFields.Length > 0)
            {
                await writer.WriteElementStringAsync(null, "lockedfields", null, string.Join('|', item.LockedFields)).ConfigureAwait(false);
            }

            await writer.WriteElementStringAsync(null, "dateadded", null, item.DateCreated.ToString(DateAddedFormat, CultureInfo.InvariantCulture)).ConfigureAwait(false);

            await writer.WriteElementStringAsync(null, "title", null, item.Name ?? string.Empty).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(item.OriginalTitle))
            {
                await writer.WriteElementStringAsync(null, "originaltitle", null, item.OriginalTitle).ConfigureAwait(false);
            }

            var people = await libraryManager.GetPeopleAsync(item, token).ConfigureAwait(false);

            var directors = people
                .Where(i => i.IsType(PersonKind.Director))
                .Select(i => i.Name?.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(i => i)
                .ToList();

            foreach (var person in directors)
            {
                await writer.WriteElementStringAsync(null, "director", null, person ?? string.Empty).ConfigureAwait(false);
            }

            var writers = people
                .Where(i => i.IsType(PersonKind.Writer))
                .Select(i => i.Name?.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(i => i)
                .ToList();

            foreach (var person in writers)
            {
                await writer.WriteElementStringAsync(null, "writer", null, person ?? string.Empty).ConfigureAwait(false);
            }

            foreach (var person in writers)
            {
                await writer.WriteElementStringAsync(null, "credits", null, person ?? string.Empty).ConfigureAwait(false);
            }

            foreach (var trailer in item.RemoteTrailers.OrderBy(t => t.Url?.Trim()))
            {
                await writer.WriteElementStringAsync(null, "trailer", null, GetOutputTrailerUrl(trailer.Url)).ConfigureAwait(false);
            }

            if (item.CommunityRating.HasValue)
            {
                await writer.WriteElementStringAsync(null, "rating", null, item.CommunityRating.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            if (item.ProductionYear.HasValue)
            {
                await writer.WriteElementStringAsync(null, "year", null, item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            var forcedSortName = item.ForcedSortName;
            if (!string.IsNullOrEmpty(forcedSortName))
            {
                await writer.WriteElementStringAsync(null, "sorttitle", null, forcedSortName).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(item.OfficialRating))
            {
                await writer.WriteElementStringAsync(null, "mpaa", null, item.OfficialRating).ConfigureAwait(false);
            }

            if (item is IHasAspectRatio hasAspectRatio
                && !string.IsNullOrEmpty(hasAspectRatio.AspectRatio))
            {
                await writer.WriteElementStringAsync(null, "aspectratio", null, hasAspectRatio.AspectRatio).ConfigureAwait(false);
            }

            if (item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbCollection))
            {
                await writer.WriteElementStringAsync(null, "collectionnumber", null, tmdbCollection).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.TmdbCollection.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.Imdb, out var imdb))
            {
                if (item is Series)
                {
                    await writer.WriteElementStringAsync(null, "imdb_id", null, imdb).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteElementStringAsync(null, "imdbid", null, imdb).ConfigureAwait(false);
                }

                writtenProviderIds.Add(MetadataProvider.Imdb.ToString());
            }

            // Series xml saver already saves this
            if (item is not Series)
            {
                if (item.TryGetProviderId(MetadataProvider.Tvdb, out var tvdb))
                {
                    await writer.WriteElementStringAsync(null, "tvdbid", null, tvdb).ConfigureAwait(false);
                    writtenProviderIds.Add(MetadataProvider.Tvdb.ToString());
                }
            }

            if (item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb))
            {
                await writer.WriteElementStringAsync(null, "tmdbid", null, tmdb).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.Tmdb.ToString());
            }

            if (!string.IsNullOrEmpty(item.PreferredMetadataLanguage))
            {
                await writer.WriteElementStringAsync(null, "language", null, item.PreferredMetadataLanguage).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(item.PreferredMetadataCountryCode))
            {
                await writer.WriteElementStringAsync(null, "countrycode", null, item.PreferredMetadataCountryCode).ConfigureAwait(false);
            }

            if (item.PremiereDate.HasValue && item is not Episode)
            {
                var formatString = options.ReleaseDateFormat;

                if (item is MusicArtist)
                {
                    await writer.WriteElementStringAsync(
                        null,
                        "formed",
                        null,
                        item.PremiereDate.Value.ToString(formatString, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteElementStringAsync(
                        null,
                        "premiered",
                        null,
                        item.PremiereDate.Value.ToString(formatString, CultureInfo.InvariantCulture)).ConfigureAwait(false);

                    await writer.WriteElementStringAsync(
                        null,
                        "releasedate",
                        null,
                        item.PremiereDate.Value.ToString(formatString, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
            }

            if (item.EndDate.HasValue)
            {
                if (item is not Episode)
                {
                    var formatString = options.ReleaseDateFormat;

                    await writer.WriteElementStringAsync(
                        null,
                        "enddate",
                        null,
                        item.EndDate.Value.ToString(formatString, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
            }

            if (item.CriticRating.HasValue)
            {
                await writer.WriteElementStringAsync(
                    null,
                    "criticrating",
                    null,
                    item.CriticRating.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            if (item is IHasDisplayOrder hasDisplayOrder)
            {
                if (!string.IsNullOrEmpty(hasDisplayOrder.DisplayOrder))
                {
                    await writer.WriteElementStringAsync(null, "displayorder", null, hasDisplayOrder.DisplayOrder).ConfigureAwait(false);
                }
            }

            // Use original runtime here, actual file runtime later in MediaInfo
            var runTimeTicks = item.RunTimeTicks;

            if (runTimeTicks.HasValue)
            {
                var timespan = TimeSpan.FromTicks(runTimeTicks.Value);

                await writer.WriteElementStringAsync(
                    null,
                    "runtime",
                    null,
                    Convert.ToInt64(timespan.TotalMinutes).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(item.Tagline))
            {
                await writer.WriteElementStringAsync(null, "tagline", null, item.Tagline).ConfigureAwait(false);
            }

            foreach (var country in item.ProductionLocations.Trimmed().OrderBy(country => country))
            {
                await writer.WriteElementStringAsync(null, "country", null, country).ConfigureAwait(false);
            }

            foreach (var genre in item.Genres.Trimmed().OrderBy(genre => genre))
            {
                await writer.WriteElementStringAsync(null, "genre", null, genre).ConfigureAwait(false);
            }

            foreach (var studio in item.Studios.Trimmed().OrderBy(studio => studio))
            {
                await writer.WriteElementStringAsync(null, "studio", null, studio).ConfigureAwait(false);
            }

            foreach (var tag in item.Tags.Trimmed().OrderBy(tag => tag))
            {
                if (item is MusicAlbum || item is MusicArtist)
                {
                    await writer.WriteElementStringAsync(null, "style", null, tag).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteElementStringAsync(null, "tag", null, tag).ConfigureAwait(false);
                }
            }

            if (item.TryGetProviderId(MetadataProvider.AudioDbArtist, out var externalId))
            {
                await writer.WriteElementStringAsync(null, "audiodbartistid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.AudioDbArtist.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.AudioDbAlbum, out externalId))
            {
                await writer.WriteElementStringAsync(null, "audiodbalbumid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.AudioDbAlbum.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.Zap2It, out externalId))
            {
                await writer.WriteElementStringAsync(null, "zap2itid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.Zap2It.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.MusicBrainzAlbum, out externalId))
            {
                await writer.WriteElementStringAsync(null, "musicbrainzalbumid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.MusicBrainzAlbum.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.MusicBrainzAlbumArtist, out externalId))
            {
                await writer.WriteElementStringAsync(null, "musicbrainzalbumartistid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.MusicBrainzAlbumArtist.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.MusicBrainzArtist, out externalId))
            {
                await writer.WriteElementStringAsync(null, "musicbrainzartistid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.MusicBrainzArtist.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.MusicBrainzReleaseGroup, out externalId))
            {
                await writer.WriteElementStringAsync(null, "musicbrainzreleasegroupid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.MusicBrainzReleaseGroup.ToString());
            }

            if (item.TryGetProviderId(MetadataProvider.TvRage, out externalId))
            {
                await writer.WriteElementStringAsync(null, "tvrageid", null, externalId).ConfigureAwait(false);
                writtenProviderIds.Add(MetadataProvider.TvRage.ToString());
            }

            if (item.ProviderIds is not null)
            {
                foreach (var providerKey in item.ProviderIds.Keys.OrderBy(providerKey => providerKey))
                {
                    var providerId = item.ProviderIds[providerKey];
                    if (!string.IsNullOrEmpty(providerId) && !writtenProviderIds.Contains(providerKey))
                    {
                        try
                        {
                            var tagName = GetTagForProviderKey(providerKey);
                            Logger.LogDebug("Verifying custom provider tagname {0}", tagName);
                            XmlConvert.VerifyName(tagName);
                            Logger.LogDebug("Saving custom provider tagname {0}", tagName);

                            await writer.WriteElementStringAsync(null, tagName, null, providerId).ConfigureAwait(false);
                        }
                        catch (ArgumentException)
                        {
                            // catch invalid names without failing the entire operation
                        }
                        catch (XmlException)
                        {
                            // catch invalid names without failing the entire operation
                        }
                    }
                }
            }

            if (options.SaveImagePathsInNfo)
            {
                AddImages(item, writer, libraryManager);
            }

            AddUserData(item, writer, userManager, userDataRepo, options);

            if (item is not MusicAlbum && item is not MusicArtist)
            {
                AddActors(people, writer, libraryManager, options.SaveImagePathsInNfo);
            }

            if (item is BoxSet folder)
            {
                AddCollectionItems(folder, writer);
            }
        }

        private void AddCollectionItems(Folder item, XmlWriter writer)
        {
            var items = item.LinkedChildren
                .Where(i => i.Type == LinkedChildType.Manual)
                .OrderBy(i => i.Path?.Trim())
                .ThenBy(i => i.LibraryItemId?.Trim())
                .ToList();

            foreach (var link in items)
            {
                writer.WriteStartElement("collectionitem");

                if (!string.IsNullOrWhiteSpace(link.Path))
                {
                    writer.WriteElementString("path", link.Path);
                }

                if (!string.IsNullOrWhiteSpace(link.LibraryItemId))
                {
                    writer.WriteElementString("ItemId", link.LibraryItemId);
                }

                writer.WriteEndElement();
            }
        }

        /// <summary>
        /// Gets the output trailer URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <returns>System.String.</returns>
        private string GetOutputTrailerUrl(string url)
        {
            // This is what xbmc expects
            return url.Replace(YouTubeWatchUrl, "plugin://plugin.video.youtube/play/?video_id=", StringComparison.OrdinalIgnoreCase);
        }

        private void AddImages(BaseItem item, XmlWriter writer, ILibraryManager libraryManager)
        {
            writer.WriteStartElement("art");

            var image = item.GetImageInfo(ImageType.Primary, 0);

            if (image is not null)
            {
                writer.WriteElementString("poster", GetImagePathToSave(image, libraryManager));
            }

            foreach (var backdrop in item.GetImages(ImageType.Backdrop).OrderBy(b => b.Path?.Trim()))
            {
                writer.WriteElementString("fanart", GetImagePathToSave(backdrop, libraryManager));
            }

            writer.WriteEndElement();
        }

        private void AddUserData(BaseItem item, XmlWriter writer, IUserManager userManager, IUserDataManager userDataRepo, XbmcMetadataOptions options)
        {
            var userId = options.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return;
            }

            var user = userManager.GetUserById(Guid.Parse(userId));

            if (user is null)
            {
                return;
            }

            if (item.IsFolder)
            {
                return;
            }

            var userdata = userDataRepo.GetUserData(user, item);

            if (userdata is not null)
            {
                writer.WriteElementString(
                    "isuserfavorite",
                    userdata.IsFavorite.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());

                if (userdata.Rating.HasValue)
                {
                    writer.WriteElementString(
                        "userrating",
                        userdata.Rating.Value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
                }

                if (!item.IsFolder)
                {
                    writer.WriteElementString(
                        "playcount",
                        userdata.PlayCount.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString(
                        "watched",
                        userdata.Played.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());

                    if (userdata.LastPlayedDate.HasValue)
                    {
                        writer.WriteElementString(
                            "lastplayed",
                            userdata.LastPlayedDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).ToLowerInvariant());
                    }

                    writer.WriteStartElement("resume");

                    var runTimeTicks = item.RunTimeTicks ?? 0;

                    writer.WriteElementString(
                        "position",
                        TimeSpan.FromTicks(userdata.PlaybackPositionTicks).TotalSeconds.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString(
                        "total",
                        TimeSpan.FromTicks(runTimeTicks).TotalSeconds.ToString(CultureInfo.InvariantCulture));
                }
            }

            writer.WriteEndElement();
        }

        private void AddActors(IReadOnlyList<PersonInfo> people, XmlWriter writer, ILibraryManager libraryManager, bool saveImagePath)
        {
            foreach (var person in people
                .OrderBy(person => person.SortOrder ?? 0)
                .ThenBy(person => person.Name?.Trim()))
            {
                if (person.IsType(PersonKind.Director) || person.IsType(PersonKind.Writer))
                {
                    continue;
                }

                writer.WriteStartElement("actor");

                if (!string.IsNullOrWhiteSpace(person.Name))
                {
                    writer.WriteElementString("name", person.Name);
                }

                if (!string.IsNullOrWhiteSpace(person.Role))
                {
                    writer.WriteElementString("role", person.Role);
                }

                if (person.Type != PersonKind.Unknown)
                {
                    writer.WriteElementString("type", person.Type.ToString());
                }

                if (person.SortOrder.HasValue)
                {
                    writer.WriteElementString(
                        "sortorder",
                        person.SortOrder.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (saveImagePath)
                {
                    var personEntity = libraryManager.GetPerson(person.Name);
                    var image = personEntity?.GetImageInfo(ImageType.Primary, 0);

                    if (image is not null)
                    {
                        writer.WriteElementString(
                            "thumb",
                            GetImagePathToSave(image, libraryManager));
                    }
                }

                writer.WriteEndElement();
            }
        }

        private string GetImagePathToSave(ItemImageInfo image, ILibraryManager libraryManager)
        {
            if (!image.IsLocalFile)
            {
                return image.Path;
            }

            return libraryManager.GetPathAfterNetworkSubstitution(image.Path);
        }

        private void AddCustomTags(string path, IReadOnlyCollection<string> xmlTagsUsed, XmlWriter writer, ILogger<BaseNfoSaver> logger)
        {
            var settings = new XmlReaderSettings()
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using (var fileStream = File.OpenRead(path))
            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                try
                {
                    reader.MoveToContent();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error reading existing xml tags from {Path}.", path);
                    return;
                }

                reader.Read();

                // Loop through each element
                while (!reader.EOF && reader.ReadState == ReadState.Interactive)
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        var name = reader.Name;

                        if (!_commonTags.Contains(name)
                            && !xmlTagsUsed.Contains(name, StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WriteNode(reader, false);
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }
        }

        private string GetTagForProviderKey(string providerKey)
            => providerKey.ToLowerInvariant() + "id";

        protected static string SortNameOrName(BaseItem item)
        {
            if (item is null)
            {
                return string.Empty;
            }

            if (item.SortName is not null)
            {
                string trimmed = item.SortName.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return (item.Name ?? string.Empty).Trim();
        }
    }
}
