namespace IPFilter
{
    class UTorrentApplication : BitTorrentApplication
    {
        protected override string DefaultDisplayName { get { return "�Torrent"; } }
        protected override string FolderName { get { return "uTorrent"; } }
    }
}