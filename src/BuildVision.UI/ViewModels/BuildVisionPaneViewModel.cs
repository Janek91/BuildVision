﻿using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BuildVision.Common;
using BuildVision.Common.Logging;
using BuildVision.Contracts;
using BuildVision.Contracts.Exceptions;
using BuildVision.Contracts.Models;
using BuildVision.Core;
using BuildVision.Exports.Providers;
using BuildVision.Exports.Services;
using BuildVision.Exports.ViewModels;
using BuildVision.Helpers;
using BuildVision.UI.DataGrid;
using BuildVision.UI.Helpers;
using BuildVision.UI.Models;
using BuildVision.UI.Settings.Models;
using BuildVision.Views.Settings;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Threading;
using Serilog;
using Process = System.Diagnostics.Process;
using SortDescription = BuildVision.UI.Settings.Models.Sorting.SortDescription;

namespace BuildVision.UI.ViewModels
{
    [Export(typeof(IBuildVisionPaneViewModel))]
    public class BuildVisionPaneViewModel : BindableBase, IBuildVisionPaneViewModel
    {
        private ProjectItem _selectedProjectItem;
        private readonly IBuildInformationProvider _buildInformationProvider;
        private readonly IBuildService _buildService;
        private readonly IErrorNavigationService _errorNavigationService;
        private readonly ITaskBarInfoService _taskBarInfoService;
        private readonly IPackageSettingsProvider _settingsProvider;
        private ObservableCollection<DataGridColumn> _gridColumnsRef;

        private readonly ILogger _logger = LogManager.ForContext<BuildVisionPaneViewModel>();

#if MARKETPLACE
        public const bool PreviewVersion = false;
#else
        public const bool PreviewVersion = true;
#endif

        public ISolutionModel SolutionModel { get; set; }

        public string GridGroupHeaderName => string.IsNullOrEmpty(GridGroupPropertyName) ? string.Empty : ControlSettings.GridSettings.Columns[GridGroupPropertyName].Header;

        public CompositeCollection GridColumnsGroupMenuItems => CreateContextMenu();

        public bool HideUpToDateTargets
        {
            get => ControlSettings.GeneralSettings.HideUpToDateTargets;
            set
            {
                SetProperty(() => ControlSettings.GeneralSettings.HideUpToDateTargets, val => ControlSettings.GeneralSettings.HideUpToDateTargets = val, value);
                ResetProjectListFilter();
            }
        }

        private void ResetProjectListFilter()
        {
            if (ControlSettings.GeneralSettings.HideUpToDateTargets || !ControlSettings.GeneralSettings.FillProjectListOnBuildBegin)
            {
                GroupedProjectsList.Filter = x =>
                {
                    var projectItem = x as ProjectItem;
                    if (ControlSettings.GeneralSettings.HideUpToDateTargets && projectItem.State == ProjectState.UpToDate)
                    {
                        return false;
                    }
                    if (!ControlSettings.GeneralSettings.FillProjectListOnBuildBegin && projectItem.State == ProjectState.Pending)
                    {
                        return false;
                    }
                    return true;
                };
            }
            else
            {
                GroupedProjectsList.Filter = null;
            }
        }

        public ControlSettings ControlSettings { get; }

        public ObservableCollection<IProjectItem> Projects { get; }

        public IBuildInformationModel BuildInformationModel { get; set; }

        public string GridGroupPropertyName
        {
            get => ControlSettings.GridSettings.GroupName;
            set
            {
                if (ControlSettings.GridSettings.GroupName != value)
                {
                    ControlSettings.GridSettings.GroupName = value;
                    GroupedProjectsList.GroupDescriptions.Clear();
                    if (!string.IsNullOrEmpty(value))
                    {
                        GroupedProjectsList.GroupDescriptions.Add(new PropertyGroupDescription(value));
                    }
                    OnPropertyChanged(nameof(GridGroupPropertyName));
                    OnPropertyChanged(nameof(GridColumnsGroupMenuItems));
                    OnPropertyChanged(nameof(GridGroupHeaderName));
                }
            }
        }

        private CompositeCollection CreateContextMenu()
        {
            var collection = new CompositeCollection
            {
                new MenuItem
                {
                    Header = Resources.NoneMenuItem,
                    Tag = string.Empty
                }
            };

            foreach (var column in ControlSettings.GridSettings.Columns.Where(ColumnsManager.ColumnIsGroupable))
            {
                string header = column.Header;
                var menuItem = new MenuItem
                {
                    Header = !string.IsNullOrEmpty(header)
                                ? header
                                : ColumnsManager.GetInitialColumnHeader(column),
                    Tag = column.PropertyNameId
                };

                collection.Add(menuItem);
            }

            foreach (MenuItem menuItem in collection)
            {
                menuItem.IsCheckable = false;
                menuItem.StaysOpenOnClick = false;
                menuItem.IsChecked = GridGroupPropertyName == (string)menuItem.Tag;
                menuItem.Command = GridGroupPropertyMenuItemClicked;
                menuItem.CommandParameter = menuItem.Tag;
            }

            return collection;
        }

        public SortDescription GridSortDescription
        {
            get => ControlSettings.GridSettings.Sort;
            set
            {
                if (ControlSettings.GridSettings.Sort != value)
                {
                    ControlSettings.GridSettings.Sort = value;
                    OnPropertyChanged(nameof(GridSortDescription));
                }
            }
        }

        // Should be initialized by View.
        public void SetGridColumnsRef(ObservableCollection<DataGridColumn> gridColumnsRef)
        {
            if (_gridColumnsRef != gridColumnsRef)
            {
                _gridColumnsRef = gridColumnsRef;
                GenerateColumns();
            }
        }

        // TODO: Rewrite using CollectionViewSource? 
        // http://stackoverflow.com/questions/11505283/re-sort-wpf-datagrid-after-bounded-data-has-changed
        public ListCollectionView GroupedProjectsList { get; }

        public DataGridHeadersVisibility GridHeadersVisibility
        {
            get => ControlSettings.GridSettings.ShowColumnsHeader
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            set
            {
                bool showColumnsHeader = (value != DataGridHeadersVisibility.None);
                if (ControlSettings.GridSettings.ShowColumnsHeader != showColumnsHeader)
                {
                    ControlSettings.GridSettings.ShowColumnsHeader = showColumnsHeader;
                    OnPropertyChanged(nameof(GridHeadersVisibility));
                }
            }
        }

        public ProjectItem SelectedProjectItem
        {
            get => _selectedProjectItem;
            set => SetProperty(ref _selectedProjectItem, value);
        }

        public BuildVisionPaneViewModel()
        {
            ControlSettings = new ControlSettings();
            BuildInformationModel = new BuildInformationModel();
            SolutionModel = new SolutionModel();
            Projects = new ObservableCollection<IProjectItem>();
        }

        [ImportingConstructor]
        public BuildVisionPaneViewModel(
            IBuildInformationProvider buildInformationProvider,
            IPackageSettingsProvider settingsProvider,
            ISolutionProvider solutionProvider,
            IBuildService buildService,
            IErrorNavigationService errorNavigationService,
            ITaskBarInfoService taskBarInfoService)
        {
            _buildInformationProvider = buildInformationProvider;
            _buildService = buildService;
            _errorNavigationService = errorNavigationService;
            _taskBarInfoService = taskBarInfoService;
            BuildInformationModel = _buildInformationProvider.BuildInformationModel;
            SolutionModel = solutionProvider.GetSolutionModel();
            ControlSettings = settingsProvider.Settings;
            Projects = _buildInformationProvider.Projects;
            GroupedProjectsList = CollectionViewSource.GetDefaultView(Projects) as ListCollectionView;
            ResetProjectListFilter();
            if (!string.IsNullOrWhiteSpace(GridGroupPropertyName))
            {
                GroupedProjectsList.GroupDescriptions.Add(new PropertyGroupDescription(GridGroupPropertyName));
            }
            GroupedProjectsList.CustomSort = SortOrderFactory.GetProjectItemSorter(GridSortDescription);
            GroupedProjectsList.IsLiveGrouping = true;
            GroupedProjectsList.IsLiveSorting = true;

            _buildInformationProvider.BuildStateChanged += () =>
            {
                GroupedProjectsList.Refresh();
            };

            _settingsProvider = settingsProvider;
            _settingsProvider.SettingsChanged += () =>
            {
                OnControlSettingsChanged();
                SyncColumnSettings();
            };

            ControlSettings.PropertyChanged += (sender, e) =>
            {
                OnControlSettingsChanged();
                SyncColumnSettings();
            };

            if (settingsProvider.Settings.GeneralSettings.FillProjectListOnBuildBegin)
            {
                Projects.CollectionChanged += (sender, e) =>
                {
                    OnPropertyChanged(nameof(GroupedProjectsList));
                };
            }
        }

        private void OpenContainingFolder()
        {
            try
            {
                string dir = Path.GetDirectoryName(SelectedProjectItem.FullName);
                Process.Start(dir);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to open folder '{FullName}' containing the project '{UniqueName}'.", SelectedProjectItem.FullName, SelectedProjectItem.UniqueName);
                MessageBox.Show(ex.Message + "\n\nSee log for details.", Resources.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReorderGrid(object obj)
        {
            var e = (DataGridSortingEventArgs)obj;

            ListSortDirection? oldSortDirection = e.Column.SortDirection;
            ListSortDirection? newSortDirection;
            switch (oldSortDirection)
            {
                case null:
                    newSortDirection = ListSortDirection.Ascending;
                    break;
                case ListSortDirection.Ascending:
                    newSortDirection = ListSortDirection.Descending;
                    break;
                case ListSortDirection.Descending:
                    newSortDirection = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(obj));
            }

            e.Handled = true;
            e.Column.SortDirection = newSortDirection;
            GridSortDescription = new SortDescription(newSortDirection.ToMedia(), e.Column.GetBindedProperty());
            GroupedProjectsList.CustomSort = SortOrderFactory.GetProjectItemSorter(GridSortDescription);
        }

        public void GenerateColumns() => ColumnsManager.GenerateColumns(_gridColumnsRef, ControlSettings.GridSettings);

        public void SyncColumnSettings() => ColumnsManager.SyncColumnSettings(_gridColumnsRef, ControlSettings.GridSettings);

        public void OnControlSettingsChanged()
        {
            ControlSettings.InitFrom(_settingsProvider.Settings);
            GenerateColumns();
            // Raise all properties have changed.
            OnPropertyChanged(null);
            _taskBarInfoService.ResetTaskBarInfo(false);
            ResetProjectListFilter();
        }

        private bool IsProjectItemEnabledForActions() => SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !SelectedProjectItem.IsBatchBuildProject;

        private void CopyErrorMessageToClipboard(ProjectItem projectItem)
        {
            try
            {
                var errors = new StringBuilder();
                foreach (var errorItem in projectItem.Errors)
                {
                    errors.AppendLine(string.Format("{0}({1},{2},{3},{4}): error {5}: {6}", errorItem.File, errorItem.LineNumber, errorItem.ColumnNumber, errorItem.EndLineNumber, errorItem.EndColumnNumber, errorItem.Code, errorItem.Message));
                }
                Clipboard.SetText(errors.ToString());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to CopyErrorMessageToClipboard for project '{UniqueName}'.", projectItem.UniqueName);
            }
        }

        #region Commands

        public ICommand NavigateToErrorCommand => new RelayCommand(obj => _errorNavigationService.NavigateToErrorItem(obj as ErrorItem));

        public ICommand ReportIssues => new RelayCommand(obj => GithubHelper.OpenBrowserWithPrefilledIssue());

        public ICommand GridSorting => new RelayCommand(obj => ReorderGrid(obj));

        public ICommand GridGroupPropertyMenuItemClicked => new RelayCommand(obj => GridGroupPropertyName = (obj != null) ? obj.ToString() : string.Empty);

        public ICommand SelectedProjectOpenContainingFolderAction => new RelayCommand(obj => OpenContainingFolder(), canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.FullName)));

        public ICommand SelectedProjectCopyBuildOutputFilesToClipboardAction => new RelayCommand(obj => _buildService.ProjectCopyBuildOutputFilesToClipBoard(SelectedProjectItem), canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !ControlSettings.ProjectItemSettings.CopyBuildOutputFileTypesToClipboard.IsEmpty));

        public ICommand SelectedProjectBuildAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.BuildCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectRebuildAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.RebuildCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectCleanAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.CleanCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectCopyErrorMessagesAction => new RelayCommand(obj => CopyErrorMessageToClipboard(SelectedProjectItem), canExecute: obj => SelectedProjectItem?.ErrorsCount > 0);

        public ICommand BuildSolutionAction => new RelayCommand(obj => _buildService.BuildSolution());

        public ICommand RebuildSolutionAction => new RelayCommand(obj => _buildService.RebuildSolution());

        public ICommand CleanSolutionAction => new RelayCommand(obj => _buildService.CleanSolution());

        public ICommand CancelBuildSolutionAction => new RelayCommand(obj => _buildService.CancelBuildSolution());

        public ICommand OpenGridColumnsSettingsAction => new RelayCommand(obj => ShowOptionPage?.Invoke(typeof(GridSettings)));

        public ICommand OpenGeneralSettingsAction => new RelayCommand(obj => ShowOptionPage?.Invoke(typeof(GeneralSettings)));

        #endregion

        public event Action<Type> ShowOptionPage;
    }
}
