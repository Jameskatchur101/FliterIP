namespace IPFilter.Apps
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Models;

    public interface IApplication
    {
        Task<ApplicationDetectionResult> DetectAsync();

        Task<FilterUpdateResult> UpdateFilterAsync(FilterDownloadResult filter, CancellationToken cancellationToken, IProgress<ProgressModel> progress);
    }
}