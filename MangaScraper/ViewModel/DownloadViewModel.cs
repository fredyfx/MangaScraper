﻿using System;
using Blacker.MangaScraper.Common;
using Blacker.MangaScraper.Common.Events;
using Blacker.MangaScraper.Common.Models;
using Blacker.MangaScraper.Common.Utils;
using System.Windows.Input;
using Blacker.MangaScraper.Commands;
using System.IO;
using System.Text.RegularExpressions;
using Blacker.MangaScraper.Library.Models;
using log4net;
using Blacker.MangaScraper.Helpers;
using System.Linq;

namespace Blacker.MangaScraper.ViewModel
{
    class DownloadViewModel : BaseViewModel
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(DownloadViewModel));

        private const string ButtonCancelText = "Cancel";
        private const string ButtonCancellingText = "Canceling";

        private const string DownloadedChapterNotAvailable = "Chapter was not found in the download folder.";

        private readonly IScraper _scraper;
        private readonly IDownloader _downloader;

        private readonly DownloadedChapterInfo _downloadInfo;
        private readonly ISemaphore _downloadSemaphore;

        private readonly RelayCommand _cancelDownloadCommand;
        private readonly RelayCommand _removeDownloadCommand;
        private readonly RelayCommand _openDownloadCommand;
        private readonly RelayCommand _retryDownloadCommand;

        private DownloadState _downloadState;
        private int _progressValue;
        private string _cancelText;
        private string _currentActionText;
        private bool _completed;

        private bool? _downloadExists = null;

        public DownloadViewModel(DownloadedChapterInfo downloadInfo, ISemaphore downloadSemaphore)
        {
            if (downloadInfo == null)
                throw new ArgumentNullException("downloadInfo");
            
            if (downloadSemaphore == null) 
                throw new ArgumentNullException("downloadSemaphore");

            if (downloadInfo.ChapterRecord == null)
                throw new ArgumentException("Chapter record is invalid.", "downloadInfo");

            if (String.IsNullOrEmpty(downloadInfo.ChapterRecord.ChapterId))
                throw new ArgumentException("Chapter record id is invalid.", "downloadInfo");

            if (downloadInfo.ChapterRecord.MangaRecord == null)
                throw new ArgumentException("Manga record is invalid.", "downloadInfo");

            if (String.IsNullOrEmpty(downloadInfo.ChapterRecord.MangaRecord.MangaId))
                throw new ArgumentException("Manga record id is invalid.", "downloadInfo");

            _downloadInfo = downloadInfo;
            _downloadSemaphore = downloadSemaphore;

            _scraper = ScraperLoader.Instance.AllScrapers.FirstOrDefault(s => s.ScraperGuid == downloadInfo.ChapterRecord.Scraper);

            if (_scraper != null)
            {
                _downloader = _scraper.GetDownloader();

                // register downloader events
                _downloader.DownloadProgress += _downloader_DownloadProgress;
                _downloader.DownloadCompleted += _downloader_DownloadCompleted;
            }

            if (!String.IsNullOrEmpty(_downloadInfo.Path))
            {
                // file was already downloaded
                State = DownloadState.Unknown;
                Completed = true;
            }
            else
            {
                // we will be downloading the file now
                State = DownloadState.Ok;
                Completed = false;
            }

            CurrentActionText = String.Empty;

            _cancelDownloadCommand = new RelayCommand(Cancel, x => !Completed);
            _removeDownloadCommand = new RelayCommand(Remove);
            _openDownloadCommand = new RelayCommand(Open, x => DownloadExists);
            _retryDownloadCommand = new RelayCommand(RetryDownload, x => _downloader != null
                                                                         && Completed
                                                                         && !DownloadExists
                                                                         && !String.IsNullOrEmpty(_downloadInfo.DownloadFolder));

            CancelText = ButtonCancelText;
        }

        public event EventHandler<EventArgs<DownloadedChapterInfo>> DownloadCompleted;
        public event EventHandler<EventArgs<DownloadedChapterInfo>> RemoveFromCollection;
        public event EventHandler<EventArgs> DownloadStarted;

        public ICommand CancelDownloadCommand { get { return _cancelDownloadCommand; } }
        public ICommand RemoveDownloadCommand { get { return _removeDownloadCommand; } }
        public ICommand OpenDownloadCommand { get { return _openDownloadCommand; } }
        public ICommand RetryDownloadCommand { get { return _retryDownloadCommand; } }

        public string CancelText
        {
            get { return _cancelText; }
            set
            {
                _cancelText = value;
                OnPropertyChanged(() => CancelText);
            }
        }

        public int ProgressValue
        {
            get { return _progressValue; }
            set
            {
                _progressValue = value;
                OnPropertyChanged(() => ProgressValue);
            }
        }

        public IChapterRecord Chapter { get { return _downloadInfo.ChapterRecord; } }

        public IDownloader Downloader { get { return _downloader; } }

        public DateTime Downloaded { get { return _downloadInfo.Downloaded; } }

        public string ScraperName { get { return _scraper != null ? _scraper.Name : String.Empty; } }

        public string CurrentActionText
        {
            get { return _currentActionText; }
            set
            {
                _currentActionText = value;
                OnPropertyChanged(() => CurrentActionText);
            }
        }

        public bool Completed
        {
            get { return _completed; }
            private set
            {
                _completed = value;
                OnPropertyChanged(() => Completed);
            }
        }

        private DownloadState State 
        {
            get
            {
                // if the download state is unknown check if by any chance the file is not already downloaded
                if (_downloadState == DownloadState.Unknown)
                    return DownloadExists ? DownloadState.Ok : DownloadState.NotFound;

                return _downloadState;
            }
            set
            {
                _downloadState = value;
                OnPropertyChanged(() => StateColor);
            } 
        }

        public string StateColor
        {
            get
            {
                switch (State)
                {
                    case DownloadState.Ok:
                        return "YellowGreen";
                    case DownloadState.Cancelled:
                    case DownloadState.NotFound:
                        return "Orange";
                    case DownloadState.Error:
                        return "Crimson";
                    default:
                        return "Crimson";
                }
            }
        }

        public bool DownloadExists
        {
            get
            {
                if (_downloadExists.HasValue)
                    return _downloadExists.Value;

                _downloadExists = (!String.IsNullOrEmpty(_downloadInfo.Path)) && (Directory.Exists(_downloadInfo.Path) || File.Exists(_downloadInfo.Path));

                if (CurrentActionText == String.Empty)
                {
                    if (_downloadExists.Value)
                    {
                        ProgressValue = 100;
                        CurrentActionText = _downloadInfo.Path;
                    }
                    else
                    {
                        CurrentActionText = DownloadedChapterNotAvailable;
                    }
                }

                OnPropertyChanged(() => CurrentActionText);

                return _downloadExists.Value;
            }
        }

        public void DownloadChapter(string outputPath, IDownloadFormatProvider formatProvider)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Invalid output path", "outputPath");

            if (formatProvider == null) 
                throw new ArgumentNullException("formatProvider");

            if (Downloader == null)
                throw new InvalidOperationException("There is no downloader configured for the chapter's scraper.");

            _downloadInfo.DownloadFolder = outputPath;

            Downloader.DownloadChapterAsync(_downloadSemaphore, Chapter, _downloadInfo.DownloadFolder, formatProvider);
        }

        public void Open(object parameter)
        {
            if (!Completed || State != DownloadState.Ok)
                throw new InvalidOperationException();

            try
            {
                bool isFolder = Directory.Exists(_downloadInfo.Path);

                if (!isFolder && !String.IsNullOrEmpty(Properties.Settings.Default.ReaderPath))
                {
                    System.Diagnostics.Process.Start(Properties.Settings.Default.ReaderPath, ProcessHelper.EscapeArguments(_downloadInfo.Path));
                }
                else
                {
                    System.Diagnostics.Process.Start(_downloadInfo.Path);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Unable to open downloaded chapter.", ex);
            }
        }

        public void Cancel(object parameter)
        {
            if (Downloader != null)
            {
                _downloader.Cancel();

                CancelText = ButtonCancellingText;
            }
        }

        public void Remove(object parameter)
        {
            if (Downloader != null)
                _downloader.Cancel();

            OnRemoveFromCollection();
        }

        public void RetryDownload(object parameter)
        {
            if (Downloader == null)
                throw new InvalidOperationException("There is no downloader configured for the chapter's scraper.");

            // we will be downloading the file now
            State = DownloadState.Ok;
            Completed = false;
            CancelText = ButtonCancelText;
            _cancelDownloadCommand.Disabled = false;
            _retryDownloadCommand.Disabled = true;

            IDownloadFormatProvider formatProvider = ScraperLoader.Instance.GetFirstOrDefaultDownloadFormatProvider(_downloadInfo.DownloadFormatProviderId);

            Downloader.DownloadChapterAsync(_downloadSemaphore, Chapter, _downloadInfo.DownloadFolder, formatProvider);
        }

        void _downloader_DownloadProgress(object sender, DownloadProgressEventArgs e)
        {
            if(ProgressValue == 0 && e.PercentComplete > 0)
                OnDownloadStarted();

            ProgressValue = e.PercentComplete;
            CurrentActionText = e.Message;
        }

        void _downloader_DownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                CurrentActionText = "Download was cancelled.";
                State = DownloadState.Cancelled;
            }
            else if (e.Error != null)
            {
                CurrentActionText = "Unable to download/save requested chaper";
                State = DownloadState.Error;
                _log.Error("Unable to download/save requested chapter.", e.Error);
            }
            else
            {
                State = DownloadState.Ok;
                _openDownloadCommand.Disabled = false;
                _downloadInfo.Path = e.DownloadedPath;
            }

            _downloadInfo.Downloaded = DateTime.UtcNow;
            Completed = true;
            _cancelDownloadCommand.Disabled = true;
            _downloadExists = null; // reset the download exists flag

            OnDownloadCompleted();
        }

        private void OnDownloadCompleted()
        {
            if (DownloadCompleted != null)
            {
                DownloadCompleted(this, new EventArgs<DownloadedChapterInfo>(_downloadInfo));
            }
        }

        private void OnRemoveFromCollection()
        {
            if (RemoveFromCollection != null)
            {
                RemoveFromCollection(this, new EventArgs<DownloadedChapterInfo>(_downloadInfo));
            }
        }

        private void OnDownloadStarted()
        {
            if (DownloadStarted != null)
            {
                DownloadStarted(this, EventArgs.Empty);
            }
        }

        private enum DownloadState
        {
            Unknown,
            Ok,
            Cancelled,
            NotFound,
            Error
        }

    }
}
