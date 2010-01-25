using System.Collections.Generic;

namespace IPFilter
{
    public class SourceForgeMirrorProvider : IMirrorProvider
    {
        readonly SourceForgeMirrorListDownloader listDownloader;
        readonly SourceForgeMirrorParser parser;

        //public const string DefaultMirrorListUrl = "http://sourceforge.net/project/mirror_picker.php?height=350&width=300&group_id=92411&filesize=&filename=ipfilter.zip&abmode=&modal=1&_=1244453588655";
        public const string DefaultMirrorListUrl = "http://sourceforge.net/settings/mirror_choices?projectname=emulepawcio&filename=Ipfilter/Ipfilter/ipfilter.zip";

        public SourceForgeMirrorProvider() : this( new SourceForgeMirrorParser(), DefaultMirrorListUrl ) {}

        public SourceForgeMirrorProvider(SourceForgeMirrorParser mirrorParser, string mirrorListUrl)
            : this(mirrorParser, new SourceForgeMirrorListDownloader(mirrorListUrl)) {}

        public SourceForgeMirrorProvider(SourceForgeMirrorParser parser, SourceForgeMirrorListDownloader downloader)
        {
            listDownloader = downloader;
            this.parser = parser;
            Name = "SourceForge.net";
        }

        /// <summary>
        /// The name of the mirror provider
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets a list of mirrors for this provider
        /// </summary>
        /// <returns></returns>
        public IEnumerable<FileMirror> GetMirrors()
        {
            return GetMirrors(DownloadMirrorList());
        }

        public override string ToString()
        {
            return Name;
        }

        /// <summary>
        /// Gets a list of mirrors for this provider from the passed HTML
        /// </summary>
        /// <param name="html">The HTML containing the mirror list</param>
        /// <returns></returns>
        public IEnumerable<FileMirror> GetMirrors(string html)
        {
            return parser.ParseMirrors(html);
        }

        /// <summary>
        /// Downloads the list of mirrors
        /// </summary>
        /// <returns></returns>
        public string DownloadMirrorList()
        {
            return listDownloader.Download();
        }
    }
}