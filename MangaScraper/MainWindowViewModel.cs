﻿using System;
using System.Collections.Generic;
using System.Linq;
using Blacker.Scraper;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Blacker.MangaScraper.Commands;
using Blacker.Scraper.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Blacker.MangaScraper.Helpers;

namespace Blacker.MangaScraper
{
    class MainWindowViewModel : BaseViewModel, ICleanup
    {
        private readonly IList<IScraper> _scrapers;

        private IScraper _currentScraper;

        private AsyncRequestQueue _requestQueue;

        private readonly ICommand _searchCommand;
        private readonly ICommand _browseCommand;
        private readonly ICommand _saveCommand;

        private MangaRecord _selectedManga;

        private string _outputPath;
        private string _searchString = string.Empty;

        private static readonly Regex _invalidPathCharsRegex = new Regex(string.Format("[{0}]",
                                    Regex.Escape(new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars()))),
                               RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly object _syncRoot = new object();

        private readonly HashSet<IDownloader> _downloaders = new HashSet<IDownloader>();

        public MainWindowViewModel()
        {
            _searchCommand = new SearchCommand(this);
            _browseCommand = new BrowseCommand(this);
            _saveCommand = new SaveCommand(this);

            _scrapers = new List<IScraper>(ReflectionHelper.GetInstances<IScraper>());

            if (!string.IsNullOrEmpty(Properties.Settings.Default.SelectedScraper))
                CurrentScraper = _scrapers.FirstOrDefault(s => s.Name == Properties.Settings.Default.SelectedScraper);

            if (CurrentScraper == null)
                CurrentScraper = _scrapers.First();

            // load output path from user settings
            _outputPath = Properties.Settings.Default.OutputPath;

            Mangas = new AsyncObservableCollection<MangaRecord>();
            Chapters = new AsyncObservableCollection<ChapterRecord>();

            ZipFile = true;
            ProgressValue = 0;

            _requestQueue = new AsyncRequestQueue(System.Threading.SynchronizationContext.Current);
            _requestQueue.Initialize();
        }

        public IList<IScraper> Scrapers { get { return _scrapers; } }

        public IScraper CurrentScraper
        {
            get { return _currentScraper; }
            set
            {
                _currentScraper = value;

                // remember selected scraper
                Properties.Settings.Default.SelectedScraper = _currentScraper.Name;
                Properties.Settings.Default.Save();

                // clear lists
                if(Mangas != null)
                    Mangas.Clear();
                if(Chapters != null)
                    Chapters.Clear();
            }
        }

        public string SearchString
        {
            get { return _searchString; }
            set
            {
                _searchString = value;
                SearchMangaImmediate(_searchString);
            }
        }

        public ICommand SearchCommand { get { return _searchCommand; } }

        public ICommand BrowseCommand { get { return _browseCommand; } }

        public ICommand SaveCommand { get { return _saveCommand; } }

        public ObservableCollection<MangaRecord> Mangas { get; private set; }

        public ObservableCollection<ChapterRecord> Chapters { get; private set; }

        public string OutputPath
        {
            get { return _outputPath; }
            set
            {
                _outputPath = value;
                // remember path in user settings
                Properties.Settings.Default.OutputPath = _outputPath;
                Properties.Settings.Default.Save();
            }
        }

        public bool ZipFile { get; set; }

        public string CurrentActionText { get; set; }

        public int ProgressValue { get; set; }

        public bool ProgressIndeterminate { get; set; }

        public MangaRecord SelectedManga
        {
            get { return _selectedManga; }
            set
            {
                _selectedManga = value;
                LoadChapters(_selectedManga);
            }
        }

        public ChapterRecord SelectedChapter { get; set; }

        #region Commands

        public void SearchManga()
        {
            var scraper = CurrentScraper;
            var searchString = SearchString ?? String.Empty;

            ProgressIndeterminate = true;
            InvokePropertyChanged("ProgressIndeterminate");

            _requestQueue.Add(
                () => {
                    return scraper.GetAvailableMangas(searchString);
                },
                (r, e) => {
                    var records = r as IEnumerable<MangaRecord>;
                    if (e == null && r != null)
                    {
                        lock (_syncRoot)
                        {
                            // just replace collection -> this is easier than removing and than adding records
                            Mangas = new AsyncObservableCollection<MangaRecord>(records);
                            InvokePropertyChanged("Mangas");
                        }
                    }

                    ProgressIndeterminate = false;
                    InvokePropertyChanged("ProgressIndeterminate");
                }
            );
        }

        private void SearchMangaImmediate(string filter)
        {
            if (!(CurrentScraper is IImmediateSearchProvider) || filter == null)
                return;

            var scraper = CurrentScraper as IImmediateSearchProvider;
            var searchString = SearchString ?? String.Empty;

            ProgressIndeterminate = true;
            InvokePropertyChanged("ProgressIndeterminate");

            _requestQueue.Add(
                () =>
                {
                    return scraper.GetAvailableMangasImmediate(searchString);
                },
                (r, e) =>
                {
                    var requests = r as IEnumerable<MangaRecord>;
                    if (e == null && r != null)
                    {
                        lock (_syncRoot)
                        {
                            Mangas = new AsyncObservableCollection<MangaRecord>(requests);
                            InvokePropertyChanged("Mangas");
                        }
                    }

                    ProgressIndeterminate = false;
                    InvokePropertyChanged("ProgressIndeterminate");
                }
            );
        }

        private void LoadChapters(MangaRecord manga)
        {
            if (manga == null)
                return;

            var scraper = CurrentScraper;

            ProgressIndeterminate = true;
            InvokePropertyChanged("ProgressIndeterminate");

            _requestQueue.Add(
                () =>
                {
                    return scraper.GetAvailableChapters(manga);
                },
                (r, e) =>
                {
                    var results = r as IEnumerable<ChapterRecord>;
                    if (e == null && r != null)
                    {
                        lock (_syncRoot)
                        {
                            // just replace collection -> this is easier than removing and than adding records
                            Chapters = new AsyncObservableCollection<ChapterRecord>(results);
                            InvokePropertyChanged("Chapters");
                        }
                    }

                    ProgressIndeterminate = false;
                    InvokePropertyChanged("ProgressIndeterminate");
                }
            );
        }

        /// <summary>
        /// Browse for output folder
        /// </summary>
        public void BrowseClicked()
        {
            // WPF doesn't have folder browser dialog, so we have to use the one from Windows.Forms
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.SelectedPath = OutputPath;
                dlg.Description = "Select output directory.";
                dlg.ShowNewFolderButton = true;

                System.Windows.Forms.DialogResult result = dlg.ShowDialog();

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    OutputPath = dlg.SelectedPath;
                    InvokePropertyChanged("OutputPath");
                }
            }
        }

        public void SaveChapter()
        {
            if (string.IsNullOrEmpty(OutputPath))
            {
                MessageBox.Show("Output path must be selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (SelectedChapter == null)
            {
                MessageBox.Show("Chapter must be selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var downloader = CurrentScraper.GetDownloader();
            downloader.DownloadProgress += Downloader_DownloadProgress;
            downloader.DownloadCompleted += Downloader_DownloadCompleted;

            if (ZipFile)
                downloader.DownloadChapterAsync(SelectedChapter, new FileInfo(Path.Combine(OutputPath, GetNameForSave(SelectedChapter) + ".zip")));
            else
                downloader.DownloadChapterAsync(SelectedChapter, new DirectoryInfo(Path.Combine(OutputPath, GetNameForSave(SelectedChapter))));

            _downloaders.Add(downloader);

            ((BaseCommand)SaveCommand).Disabled = true;
            InvokePropertyChanged("SaveCommand");
        }

        #endregion // Commands

        private string GetNameForSave(ChapterRecord chapter)
        {
            string fileName = String.Format("{0} - {1}", chapter.MangaName, chapter.ChapterName).Replace(" ", "_");
            
            return _invalidPathCharsRegex.Replace(fileName, "");
        }

        void Downloader_DownloadProgress(object sender, Scraper.Events.DownloadProgressEventArgs e)
        {
            ProgressValue = e.PercentComplete;
            CurrentActionText = e.Message;

            InvokePropertyChanged("ProgressValue");
            InvokePropertyChanged("CurrentActionText");
        }

        void Downloader_DownloadCompleted(object sender, Scraper.Events.DownloadCompletedEventArgs e)
        {
            ProgressValue = 0;

            ((BaseCommand)SaveCommand).Disabled = false;

            InvokePropertyChanged("ProgressValue");
            InvokePropertyChanged("SaveCommand");

            if (e.Cancelled)
            {
                CurrentActionText = "Download was cancelled.";

                InvokePropertyChanged("CurrentActionText");
            }
            else if (e.Error != null)
            {
                MessageBox.Show("Unable to download/save requested chaper, error reason is:\n\n\"" + e.Error.Message + "\"", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            var downloader = sender as IDownloader;

            downloader.DownloadProgress -= Downloader_DownloadProgress;
            downloader.DownloadCompleted -= Downloader_DownloadCompleted;

            _downloaders.Remove(downloader);

        }

        #region ICleanup implementation

        public void Cleanup()
        {
            _requestQueue.Stop();
        }

        #endregion
    }
}
