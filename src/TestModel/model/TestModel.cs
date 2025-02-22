// ***********************************************************************
// Copyright (c) 2018 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Engine;

namespace TestCentric.Gui.Model
{
    using Services;

    public class TestModel : ITestModel
    {
        private const string PROJECT_LOADER_EXTENSION_PATH = "/NUnit/Engine/TypeExtensions/IProjectLoader";
        private const string NUNIT_PROJECT_LOADER = "NUnit.Engine.Services.ProjectLoaders.NUnitProjectLoader";
        private const string VISUAL_STUDIO_PROJECT_LOADER = "NUnit.Engine.Services.ProjectLoaders.VisualStudioProjectLoader";

        // Our event dispatcher. Events are exposed through the Events
        // property. This is used when firing events from the model.
        private TestEventDispatcher _events;

        // Check if the loaded Assemblies has been changed
        private AssemblyWatcher _assemblyWatcher;

        private bool _lastRunWasDebugRun;

        #region Constructor

        public TestModel(ITestEngine testEngine, string applicationPrefix=null)
        {
            TestEngine = testEngine;
            Services = new TestServices(testEngine, applicationPrefix);

            foreach (var node in Services.ExtensionService.GetExtensionNodes(PROJECT_LOADER_EXTENSION_PATH))
            {
                if (node.TypeName == NUNIT_PROJECT_LOADER)
                    NUnitProjectSupport = true;
                else if (node.TypeName == VISUAL_STUDIO_PROJECT_LOADER)
                    VisualStudioSupport = true;
            }

            _events = new TestEventDispatcher(this);
            _assemblyWatcher = new AssemblyWatcher();
        }

        #endregion

        #region ITestModel Implementation
        
        #region General Properties

        // Work Directory
        public string WorkDirectory { get { return TestEngine.WorkDirectory; } }

        // Event Dispatcher
        public ITestEvents Events { get { return _events; } }

        // Services provided either by the model itself or by the engine
        public ITestServices Services { get; }

        // Project Support
        public bool NUnitProjectSupport { get; }
        public bool VisualStudioSupport { get; }

        // Runtime Support
        private List<IRuntimeFramework> _runtimes;
        public IList<IRuntimeFramework> AvailableRuntimes
        {
            get
            {
                if (_runtimes == null)
                    _runtimes = GetAvailableRuntimes();

                return _runtimes;
            }
        }

        // Result Format Support
        private List<string> _resultFormats;
        public IEnumerable<string> ResultFormats
        {
            get
            {
                if (_resultFormats == null)
                {
                    _resultFormats = new List<string>();
                    foreach (string format in Services.ResultService.Formats)
                        _resultFormats.Add(format);
                }

                return _resultFormats;
            }
        }

        #endregion

        #region Current State of the Model

        // The current TestPackage loaded by the model
        public TestPackage TestPackage { get; private set; }

        public bool IsPackageLoaded { get { return TestPackage != null; } }

        // The list of files passed to the model to load.
        public List<string> TestFiles { get; } = new List<string>();

        public IDictionary<string, object> PackageOverrides { get; } = new Dictionary<string, object>();

        public TestNode Tests { get; private set; }
        public bool HasTests { get { return Tests != null; } }

        public IList<string> AvailableCategories { get; private set; }

        public bool IsTestRunning
        {
            get { return Runner != null && Runner.IsTestRunning; }
        }

        public bool HasResults
        {
            get { return Results.Count > 0; }
        }

        public List<string> SelectedCategories { get; private set; }

        public bool ExcludeSelectedCategories { get; private set; }

        public TestFilter CategoryFilter { get; private set; } = TestFilter.Empty;

        #endregion

        #region Methods

        public void NewProject()
        {
            NewProject("Dummy");
        }

        public const string PROJECT_SAVE_MESSAGE =
            "It's not yet decided if we will implement saving of projects. The alternative is to require use of the project editor, which already supports this.";

        public void NewProject(string filename)
        {
            throw new NotImplementedException(PROJECT_SAVE_MESSAGE);
        }

        public void SaveProject()
        {
            throw new NotImplementedException(PROJECT_SAVE_MESSAGE);
        }

        public void LoadTests(IList<string> files)
        {
            if (IsPackageLoaded)
                UnloadTests();

            _events.FireTestsLoading(files);

            TestFiles.Clear();
            TestFiles.AddRange(files);

            TestPackage = MakeTestPackage(files);
            _lastRunWasDebugRun = false;

            Runner = TestEngine.GetRunner(TestPackage);
            Tests = new TestNode(Runner.Explore(TestFilter.Empty));

            MapTestsToPackages();
            AvailableCategories = GetAvailableCategories();

            Results.Clear();

            _assemblyWatcher.Setup(1000, files as IList);
            _assemblyWatcher.AssemblyChanged += (path) => _events.FireTestChanged();
            _assemblyWatcher.Start();

            _events.FireTestLoaded(Tests);

            foreach (var subPackage in TestPackage.SubPackages)
                Services.RecentFiles.Latest = subPackage.FullName;
        }

        private Dictionary<string, TestPackage> _packageMap = new Dictionary<string, TestPackage>();

        private void MapTestsToPackages()
        {
            _packageMap.Clear();
            MapTestToPackage(Tests, TestPackage);
        }

        private void MapTestToPackage(TestNode test, TestPackage package)
        {
            _packageMap[test.Id] = package;
            
            for (int index = 0; index < package.SubPackages.Count && index < test.Children.Count; index++)
                MapTestToPackage(test.Children[index], package.SubPackages[index]);
        }

        public void UnloadTests()
        {
            _events.FireTestsUnloading();

            Runner.Unload();
            Runner.Dispose();
            Tests = null;
            AvailableCategories = null;
            TestPackage = null;
            TestFiles.Clear();
            Results.Clear();
            _assemblyWatcher.Stop();

            _events.FireTestUnloaded();
        }

        public void ReloadTests()
        {
            _events.FireTestsReloading();

            // NOTE: The `ITestRunner.Reload` method supported by the engine
            // has some problems, so we simulate Unload+Load. See issue #328.

            // Replace Runner in case settings changed
            Runner.Unload();
            Runner.Dispose();
            Runner = TestEngine.GetRunner(TestPackage);

            // Discover tests
            Tests = new TestNode(Runner.Explore(TestFilter.Empty));
            AvailableCategories = GetAvailableCategories();

            Results.Clear();

            _events.FireTestReloaded(Tests);
        }

        public void ReloadPackage(TestPackage package, string config)
        {
            var originalSubPackages = new List<TestPackage>(package.SubPackages);
            package.SubPackages.Clear();

            package.Settings[EnginePackageSettings.ActiveConfig] = config;
            Services.ProjectService.ExpandProjectPackage(package);

            foreach (var subPackage in package.SubPackages)
                foreach (var original in originalSubPackages)
                    if (subPackage.Name == original.Name)
                        subPackage.SetID(original.ID);

            ReloadTests();
        }

        public void RunAllTests()
        {
            RunTests(CategoryFilter);
        }

        public void RunTests(ITestItem testItem)
        {
            if (testItem == null)
                throw new ArgumentNullException("testItem");

            var filter = testItem.GetTestFilter();

            if (!CategoryFilter.IsEmpty())
                filter = Filters.MakeAndFilter(filter, CategoryFilter);

            RunTests(filter);
        }

        private void RunTests(TestFilter filter)
        {
            SetTestDebuggingFlag(false);

            Runner.RunAsync(_events, filter);
        }

        public void DebugAllTests()
        {
            DebugTests(TestFilter.Empty);
        }

        public void DebugTests(ITestItem testItem)
        {
            if (testItem != null) DebugTests(testItem.GetTestFilter());
        }

        private void DebugTests(TestFilter filter)
        {
            SetTestDebuggingFlag(true);

            Runner.RunAsync(_events, filter);
        }

        private void SetTestDebuggingFlag(bool debuggingRequested)
        {
            // We need to re-create the test runner because settings such
            // as debugging have already been passed to the test runner.
            // For performance, only do this if we did run in a different mode than last time.
            if (_lastRunWasDebugRun != debuggingRequested)
            {
                foreach (var subPackage in TestPackage.SubPackages)
                {
                    subPackage.Settings["DebugTests"] = debuggingRequested;
                }

                Runner = TestEngine.GetRunner(TestPackage);
                Runner.Load();

                // It is not strictly necessary to load the tests
                // because the runner will do that automatically, however,
                // the initial test count will be incorrect causing UI crashes.

                _lastRunWasDebugRun = debuggingRequested;
            }
        }

        public void CancelTestRun(bool force)
        {
            Runner.StopRun(force);
        }

        public void SaveResults(string filePath, string format = "nunit3")
        {
            var resultWriter = Services.ResultService.GetResultWriter(format, new object[0]);
            var results = GetResultForTest(Tests.Id);
            resultWriter.WriteResultFile(results.Xml, filePath);
        }

        public ResultNode GetResultForTest(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                ResultNode result;
                if (Results.TryGetValue(id, out result))
                    return result;
            }

            return null;
        }

        public TestPackage GetPackageForTest(string id)
        {
            return _packageMap.ContainsKey(id) 
                ? _packageMap[id] 
                : null;
        }

        public string GetActiveConfig(TestPackage package)
        {
            string activeConfig = package.GetSetting(EnginePackageSettings.ActiveConfig, "");

            if (string.IsNullOrEmpty(activeConfig))
            {
                var project = Services.ProjectService.LoadFrom(package.FullName);
                activeConfig = project.ActiveConfigName;

                if (string.IsNullOrEmpty(activeConfig) && project.ConfigNames.Count > 0)
                    activeConfig = project.ConfigNames.FirstOrDefault();
            }

            return activeConfig;
        }

        public IList<string> GetConfigNames(TestPackage package)
        {
            return Services.ProjectService.LoadFrom(package.FullName).ConfigNames;
        }

        public void ClearResults()
        {
            Results.Clear();
        }

        public void SelectCategories(IList<string> categories, bool exclude)
        {
            SelectedCategories = new List<string>(categories);
            ExcludeSelectedCategories = exclude;

            UpdateCategoryFilter();

            _events.FireCategorySelectionChanged();
        }

        private void UpdateCategoryFilter()
        {
            var catFilter = TestFilter.Empty;

            if (SelectedCategories != null && SelectedCategories.Count > 0)
            {
                catFilter = Filters.MakeCategoryFilter(SelectedCategories);

                if (ExcludeSelectedCategories)
                    catFilter = Filters.MakeNotFilter(catFilter);
            }

            CategoryFilter = catFilter;
        }

        public void NotifySelectedItemChanged(ITestItem testItem)
        {
            _events.FireSelectedItemChanged(testItem);
        }

        #endregion

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            try
            {
                if (IsPackageLoaded)
                    UnloadTests();

                if (Runner != null)
                    Runner.Dispose();

                if (TestEngine != null)
                    TestEngine.Dispose();

                if (_assemblyWatcher != null)
                    _assemblyWatcher.Dispose();
            }
            catch (NUnitEngineUnloadException)
            {
                // TODO: Figure out what to do about this
            }
        }

        #endregion

        #region Private and Internal Properties

        private ITestEngine TestEngine { get; }

        private ITestRunner Runner { get; set; }

        internal IDictionary<string, ResultNode> Results { get; } = new Dictionary<string, ResultNode>();

        #endregion

        #region Helper Methods

        // Public for testing only
        public TestPackage MakeTestPackage(IList<string> testFiles)
        {
            var package = new TestPackage(testFiles);
            var engineSettings = Services.UserSettings.Engine;

            // We use AddSetting rather than just setting the value because
            // it propagates the setting to all subprojects.

            //WIP: No longer use the process  or domain settings from earlier versions

            if (engineSettings.Agents > 0)
                package.AddSetting(EnginePackageSettings.MaxAgents, engineSettings.Agents);

            if (engineSettings.SetPrincipalPolicy)
                package.AddSetting(EnginePackageSettings.PrincipalPolicy, engineSettings.PrincipalPolicy);

            //if (Options.InternalTraceLevel != null)
            //    package.AddSetting(EnginePackageSettings.InternalTraceLevel, Options.InternalTraceLevel);

            package.AddSetting(EnginePackageSettings.ShadowCopyFiles, engineSettings.ShadowCopyFiles);

            foreach (var subpackage in package.SubPackages)
                if (Path.GetExtension(subpackage.Name) == ".sln")
                    subpackage.AddSetting(EnginePackageSettings.SkipNonTestAssemblies, true);

            foreach (var entry in PackageOverrides)
                package.AddSetting(entry.Key, entry.Value);

            return package;
        }

        // The engine returns more values than we really want.
        // For example, we don't currently distinguish between
        // client and full profiles when executing tests. We
        // drop unwanted entries here. Even if some of these items
        // are removed in a later version of the engine, we may
        // have to retain this code to work with older engines.
        private List<IRuntimeFramework> GetAvailableRuntimes()
        {
            var runtimes = new List<IRuntimeFramework>();

            foreach (var runtime in Services.GetService<IAvailableRuntimes>().AvailableRuntimes)
            {
                // Nothing below 2.0
                if (runtime.ClrVersion.Major < 2)
                    continue;

                // Remove erroneous entries for 4.5 Client profile
                if (IsErroneousNet45ClientProfile(runtime))
                    continue;

                // Skip duplicates
                var duplicate = false;
                foreach (var rt in runtimes)
                    if (rt.DisplayName == runtime.DisplayName)
                    {
                        duplicate = true;
                        break;
                    }

                if (!duplicate)
                    runtimes.Add(runtime);
            }

            runtimes.Sort((x, y) => x.DisplayName.CompareTo(y.DisplayName));

            // Now eliminate client entries where full entry follows
            for (int i = runtimes.Count - 2; i >= 0; i--)
            {
                var rt1 = runtimes[i];
                var rt2 = runtimes[i + 1];

                if (rt1.Id != rt2.Id)
                    continue;

                if (rt1.Profile == "Client" && rt2.Profile == "Full")
                    runtimes.RemoveAt(i);
            }

            return runtimes;
        }

        // Some early versions of the engine return .NET 4.5 Client profile, which doesn't really exist
        private static bool IsErroneousNet45ClientProfile(IRuntimeFramework runtime)
        {
            return runtime.FrameworkVersion.Major == 4 && runtime.FrameworkVersion.Minor > 0 && runtime.Profile == "Client";
        }

        public IList<string> GetAvailableCategories()
        {
            var categories = new Dictionary<string, string>();
            CollectCategories(Tests, categories);

            var list = new List<string>(categories.Values);
            list.Sort();
            return list;
        }

        private void CollectCategories(TestNode test, Dictionary<string, string> categories)

        {
            foreach (string name in test.GetPropertyList("Category").Split(new[] { ',' }))
                if (IsValidCategoryName(name))
                    categories[name] = name;

            if (test.IsSuite)
                foreach (TestNode child in test.Children)
                    CollectCategories(child, categories);
        }

        public static bool IsValidCategoryName(string name)
        {
            return name.Length > 0 && name.IndexOfAny(new char[] { ',', '!', '+', '-' }) < 0;
        }

        #endregion
    }
}
