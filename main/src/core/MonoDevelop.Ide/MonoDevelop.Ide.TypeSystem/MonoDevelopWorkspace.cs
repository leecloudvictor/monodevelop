//
// MonoDevelopWorkspace.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.IO;
using MonoDevelop.Core;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using MonoDevelop.Ide.Tasks;
using System.Threading.Tasks;
using MonoDevelop.Ide.Editor;
using Microsoft.CodeAnalysis.Host;
using System.Collections.Immutable;
using MonoDevelop.Core.Text;

namespace MonoDevelop.Ide.TypeSystem
{
	public class MonoDevelopWorkspace : Workspace
	{
		readonly static HostServices services = Microsoft.CodeAnalysis.Host.Mef.MefHostServices.DefaultHost;

		MonoDevelop.Projects.Solution currentMonoDevelopSolution;

		public MonoDevelopWorkspace () : base (services, "MonoDevelopWorkspace")
		{
			if (IdeApp.Workspace != null) {
				IdeApp.Workspace.ActiveConfigurationChanged += HandleActiveConfigurationChanged;
			}
		}

		protected override void Dispose (bool finalize)
		{
			base.Dispose (finalize);
			if (IdeApp.Workspace != null) {
				IdeApp.Workspace.ActiveConfigurationChanged -= HandleActiveConfigurationChanged;
			}
		}

		async void HandleActiveConfigurationChanged (object sender, EventArgs e)
		{
			if (currentMonoDevelopSolution == null)
				return;
			try {
				using (var progressMonitor = IdeApp.Workbench.ProgressMonitors.GetProjectLoadProgressMonitor (false)) {
					OnSolutionReloaded (await System.Threading.Tasks.Task.Run (() => CreateSolutionInfo (currentMonoDevelopSolution, progressMonitor)));
				}
			} catch (Exception ex) {
				LoggingService.LogError ("Error while reloading solution.", ex); 
			}
		}

		SolutionInfo CreateSolutionInfo (MonoDevelop.Projects.Solution solution, IProgressMonitor monitor)
		{
			var projects = new List<ProjectInfo> ();
			var mdProjects = solution.GetAllProjects ();
			monitor.BeginTask ("Loading projects", mdProjects.Count);
			foreach (var proj in mdProjects) {
				projects.Add (LoadProject (proj));
				monitor.Step (1);
			}
			monitor.EndTask ();
			return SolutionInfo.Create (GetSolutionId (solution), VersionStamp.Create (), solution.FileName, projects);
		}

		public Solution LoadSolution (MonoDevelop.Projects.Solution solution, IProgressMonitor progressMonitor)
		{
			this.currentMonoDevelopSolution = solution;
			var solutionInfo = CreateSolutionInfo (solution, progressMonitor);
			OnSolutionAdded (solutionInfo);
			return CurrentSolution;
		}
		
		public void UnloadSolution ()
		{
			OnSolutionRemoved (); 
		}

		Dictionary<MonoDevelop.Projects.Solution, SolutionId> solutionIdMap = new Dictionary<MonoDevelop.Projects.Solution, SolutionId> ();

		internal SolutionId GetSolutionId (MonoDevelop.Projects.Solution solution)
		{
			if (solution == null)
				throw new ArgumentNullException ("solution");
			lock (solutionIdMap) {
				SolutionId result;
				if (!solutionIdMap.TryGetValue (solution, out result)) {
					result = SolutionId.CreateNewId (solution.Name);
					solutionIdMap [solution] = result;
				}
				return result;
			}
		}

		Dictionary<MonoDevelop.Projects.Project, ProjectId> projectIdMap = new Dictionary<MonoDevelop.Projects.Project, ProjectId> ();
		Dictionary<ProjectId, ProjectData> projectDataMap = new Dictionary<ProjectId, ProjectData> ();

		internal ProjectId GetProjectId (MonoDevelop.Projects.Project p)
		{
			lock (projectIdMap) {
				ProjectId result;
				if (!projectIdMap.TryGetValue (p, out result)) {
					result = ProjectId.CreateNewId (p.Name);
					projectIdMap [p] = result;
				}
				return result;
			}
		}

		ProjectData GetProjectData (ProjectId id)
		{
			lock (projectIdMap) {
				ProjectData result;
				if (!projectDataMap.TryGetValue (id, out result)) {
					result = new ProjectData (id);
					projectDataMap [id] = result;
				}
				return result;
			}
		}

		class ProjectData
		{
			readonly ProjectId projectId;
			readonly Dictionary<string, DocumentId> documentIdMap = new Dictionary<string, DocumentId> ();

			public ProjectInfo Info {
				get;
				set;
			}

			public ProjectData (ProjectId projectId)
			{
				this.projectId = projectId;
			}

			public DocumentId GetOrCreateDocumentId (string name)
			{
				lock (documentIdMap) {
					DocumentId result;
					if (!documentIdMap.TryGetValue (name, out result)) {
						result = DocumentId.CreateNewId (projectId, name);
						documentIdMap [name] = result;
					}
					return result;
				}
			}
			
			public DocumentId GetDocumentId (string name)
			{
				DocumentId result;
				if (!documentIdMap.TryGetValue (name, out result))
					return null;
				return result;
			}

			internal void RemoveDocument (string name)
			{
				documentIdMap.Remove (name);
			}
		}

		internal DocumentId GetDocumentId (ProjectId projectId, string name)
		{
			var data = GetProjectData (projectId);
			return data.GetDocumentId (name);
		}

		public override bool CanApplyChange (ApplyChangesKind feature)
		{
			return true;
		}

		ProjectInfo LoadProject (MonoDevelop.Projects.Project p)
		{
			if (!projectIdMap.ContainsKey (p)) {
				p.FileAddedToProject += OnFileAdded;
				p.FileRemovedFromProject += OnFileRemoved;
				p.FileRenamedInProject += OnFileRenamed;
				p.Modified += OnProjectModified;
			}

			var projectId = GetProjectId (p);
			var projectData = GetProjectData (projectId); 
			var config = p.GetConfiguration (IdeApp.Workspace.ActiveConfiguration) as MonoDevelop.Projects.DotNetProjectConfiguration;
			MonoDevelop.Projects.DotNetConfigurationParameters cp = null;
			if (config != null)
				cp = config.CompilationParameters as MonoDevelop.Projects.DotNetConfigurationParameters;

			var info = ProjectInfo.Create (
				projectId,
				VersionStamp.Create (),
				p.Name,
				p.Name,
				LanguageNames.CSharp,
				p.FileName,
				p.GetOutputFileName (IdeApp.Workspace.ActiveConfiguration),
				cp != null ? cp.CreateCompilationOptions () : null,
				cp != null ? cp.CreateParseOptions () : null,
				GetDocuments (projectData, p),
				GetProjectReferences (p),
				GetMetadataReferences (p)
			);

			projectData.Info = info;
			return info;
		}

		internal static Func<string, TextLoader> CreateTextLoader = fileName => new MonoDevelopTextLoader (fileName);

		static DocumentId GetDocumentId (ProjectData id, MonoDevelop.Projects.ProjectFile f)
		{
			return id.GetOrCreateDocumentId (f.Name);
		}

		static DocumentInfo CreateDocumentInfo (ProjectData id, MonoDevelop.Projects.ProjectFile f)
		{
			return DocumentInfo.Create (GetDocumentId (id, f), f.FilePath, null, SourceCodeKind.Regular, CreateTextLoader (f.Name), f.Name, System.Text.Encoding.Default, false);
		}

		IEnumerable<DocumentInfo> GetDocuments (ProjectData id, MonoDevelop.Projects.Project p)
		{
			var duplicates = new HashSet<DocumentId> ();
			foreach (var f in p.Files) {
				if (TypeSystemParserNode.IsCompileBuildAction (f.BuildAction)) {
					if (!duplicates.Add (GetDocumentId (id, f)))
						continue;
					yield return CreateDocumentInfo (id, f);
					continue;
				}
				var mimeType = DesktopService.GetMimeTypeForUri (f.FilePath);
				var node = TypeSystemService.GetTypeSystemParserNode (mimeType, f.BuildAction);
				if (node == null || !node.Parser.CanGenerateCodeBehind (mimeType, f.BuildAction, p.SupportedLanguages))
					continue;
				if (!duplicates.Add (GetDocumentId (id, f)))
					continue;

				var textAndVersion = node.Parser.GenerateTextAndVersion (mimeType, f.BuildAction, p.SupportedLanguages, f.FilePath);

				yield return DocumentInfo.Create (
					id.GetOrCreateDocumentId (f.Name),
					f.FilePath.ChangeExtension (".g" + f.FilePath.Extension), 
					null, 
					SourceCodeKind.Regular,
					TextLoader.From (textAndVersion), 
					f.Name, 
					System.Text.Encoding.Default, 
					false
				);
			}
		}

		IEnumerable<MetadataReference> GetMetadataReferences (MonoDevelop.Projects.Project p)
		{
			var netProject = p as MonoDevelop.Projects.DotNetProject;
			if (netProject == null)
				yield break;
			var config = IdeApp.Workspace != null ? netProject.GetConfiguration (IdeApp.Workspace.ActiveConfiguration) as MonoDevelop.Projects.DotNetProjectConfiguration : null;
			bool noStdLib = false;
			if (config != null) {
				var parameters = config.CompilationParameters as MonoDevelop.Projects.DotNetConfigurationParameters;
				if (parameters != null) {
					noStdLib = parameters.NoStdLib;
				}
			}
			var hashSet = new HashSet<string> ();
			foreach (string file in netProject.GetReferencedAssemblies (MonoDevelop.Projects.ConfigurationSelector.Default, false)) {
				string fileName;
				if (!Path.IsPathRooted (file)) {
					fileName = Path.Combine (Path.GetDirectoryName (netProject.FileName), file);
				} else {
					fileName = Path.GetFullPath (file);
				}
				if (hashSet.Contains (fileName))
					continue;
				hashSet.Add (fileName);
				yield return MetadataReferenceCache.LoadReference (GetProjectId (p), fileName);
			}

			foreach (var pr in p.GetReferencedItems (MonoDevelop.Projects.ConfigurationSelector.Default)) {
				var referencedProject = pr as MonoDevelop.Projects.DotNetProject;
				if (referencedProject == null)
					continue;
				if (TypeSystemService.IsOutputTrackedProject (referencedProject)) {
					var fileName = referencedProject.GetOutputFileName (IdeApp.Workspace.ActiveConfiguration);
					yield return MetadataReferenceCache.LoadReference (GetProjectId (p), fileName);
				}
			}
		}

		IEnumerable<ProjectReference> GetProjectReferences (MonoDevelop.Projects.Project p)
		{
			foreach (var pr in p.GetReferencedItems (MonoDevelop.Projects.ConfigurationSelector.Default)) {
				var referencedProject = pr as MonoDevelop.Projects.DotNetProject;
				if (referencedProject == null)
					continue;
				if (TypeSystemService.IsOutputTrackedProject (referencedProject))
					continue;
				yield return new ProjectReference (GetProjectId (referencedProject));
			}
		}

		#region Open documents
		public override bool CanOpenDocuments {
			get {
				return true;
			}
		}

		public override void OpenDocument (DocumentId documentId, bool activate = true)
		{
			var document = CurrentSolution.GetDocument (documentId);
			if (document == null)
				return;
			MonoDevelop.Projects.Project prj = null;
			foreach (var curPrj in IdeApp.Workspace.GetAllProjects ()) {
				if (GetProjectId (curPrj) == documentId.ProjectId) {
					prj = curPrj;
					break;
				}
			}
			IdeApp.Workbench.OpenDocument (new MonoDevelop.Ide.Gui.FileOpenInformation (
				document.FilePath,
				prj,
				activate
			)); 
		}
		List<MonoDevelopSourceTextContainer> openDocuments = new List<MonoDevelopSourceTextContainer>();
		internal void InformDocumentOpen (DocumentId documentId, ITextDocument editor)
		{
			var document = CurrentSolution.GetDocument (documentId);
			if (document == null)
				return;
			var monoDevelopSourceTextContainer = new MonoDevelopSourceTextContainer (documentId, editor);
			lock (openDocuments) {
				openDocuments.Add (monoDevelopSourceTextContainer);
			}
			OnDocumentOpened (documentId, monoDevelopSourceTextContainer); 
		}

		protected override void OnDocumentTextChanged (Document document)
		{
			base.OnDocumentTextChanged (document);
		}

		protected override void OnDocumentClosing (DocumentId documentId)
		{
			base.OnDocumentClosing (documentId);
			lock (openDocuments) {
				var openDoc = openDocuments.FirstOrDefault (d => d.Id == documentId);
				if (openDoc != null) {
//					openDoc.TextChanged -= HandleTextChanged;
					openDocuments.Remove (openDoc);
				}
			}
		}

//		internal override bool CanChangeActiveContextDocument {
//			get {
//				return true;
//			}
//		}

		public void InformDocumentClose (DocumentId analysisDocument, string filePath)
		{
			try {
				OnDocumentClosed (analysisDocument, new MonoDevelopTextLoader (filePath)); 
			} catch (Exception e) {
				LoggingService.LogError ("Exception while closing document.", e); 
			}
		}
		
		public override void CloseDocument (DocumentId documentId)
		{
		}
		
		protected override void ChangedDocumentText(DocumentId documentId, SourceText text)
		{
			var document = CurrentSolution.GetDocument (documentId);
			
			if (document == null)
				return;
			var data = TextFileProvider.Instance.GetTextEditorData (document.FilePath);

			foreach (var change in text.GetTextChanges (document.GetTextAsync ().Result).OrderByDescending (c => c.Span.Start)) {
				data.ReplaceText (change.Span.Start, change.Span.Length, change.NewText);
			}
			
			OnDocumentTextChanged (documentId, text, PreservationMode.PreserveValue);
		}
		#endregion

		public Document GetDocument (DocumentId documentId, CancellationToken cancellationToken = default(CancellationToken))
		{
			var projectData = GetProjectData (documentId.ProjectId); 
			if (projectData == null)
				return null;

			return CurrentSolution.GetDocument (documentId); 
		}

		public void UpdateFileContent (string fileName, string text)
		{
			SourceText newText = SourceText.From (text);
			foreach (var project in this.projectDataMap.Keys) {
				var docId = this.GetDocumentId (project, fileName);
				if (docId == null)
					continue;
				base.OnDocumentTextChanged (docId, newText, PreservationMode.PreserveIdentity);
			}
		}

		public void AddProject (MonoDevelop.Projects.Project project)
		{
			var info = LoadProject (project);
			OnProjectAdded (info); 
		}

		public void RemoveProject (MonoDevelop.Projects.Project project)
		{
			var id = GetProjectId (project); 
			if (id != null) {
				OnProjectRemoved (id);
				projectIdMap.Remove (project);
				projectDataMap.Remove (id);
				MetadataReferenceCache.RemoveReferences (id);

				project.FileAddedToProject -= OnFileAdded;
				project.FileRemovedFromProject -= OnFileRemoved;
				project.FileRenamedInProject -= OnFileRenamed;
				project.Modified -= OnProjectModified;
			}
		}
	
		#region Project modification handlers

		void OnFileAdded (object sender, MonoDevelop.Projects.ProjectFileEventArgs args)
		{
			var project = (MonoDevelop.Projects.Project)sender;
			foreach (MonoDevelop.Projects.ProjectFileEventInfo fargs in args) {
				if (!TypeSystemParserNode.IsCompileBuildAction (fargs.ProjectFile.BuildAction))
					continue;
				var projectId = GetProjectId (project);
				var newDocument = CreateDocumentInfo (GetProjectData (projectId), fargs.ProjectFile);
				OnDocumentAdded (newDocument);
			}
		}

		void OnFileRemoved (object sender, MonoDevelop.Projects.ProjectFileEventArgs args)
		{
			var project = (MonoDevelop.Projects.Project)sender;
			foreach (MonoDevelop.Projects.ProjectFileEventInfo fargs in args) {
				var projectId = GetProjectId (project);
				var data = GetProjectData (projectId);
				var id = data.GetDocumentId (fargs.ProjectFile.FilePath); 
				if (id != null) {
					OnDocumentRemoved (id);
					data.RemoveDocument (fargs.ProjectFile.FilePath);
				}
			}
		}

		void OnFileRenamed (object sender, MonoDevelop.Projects.ProjectFileRenamedEventArgs args)
		{
			var project = (MonoDevelop.Projects.Project)sender;
			foreach (MonoDevelop.Projects.ProjectFileRenamedEventInfo fargs in args) {
				if (!TypeSystemParserNode.IsCompileBuildAction (fargs.ProjectFile.BuildAction))
					continue;

				var projectId = GetProjectId (project);
				var data = GetProjectData (projectId);

				var id = data.GetDocumentId (fargs.OldName); 
				if (id != null) {
					OnDocumentRemoved (id);
					data.RemoveDocument (fargs.OldName);
				}

				var newDocument = CreateDocumentInfo (GetProjectData (projectId), fargs.ProjectFile);
				OnDocumentAdded (newDocument);
			}
		}

		void OnProjectModified (object sender, MonoDevelop.Projects.SolutionItemModifiedEventArgs args)
		{
			if (!args.Any (x => x.Hint == "TargetFramework" || x.Hint == "References"))
				return;
			var project = (MonoDevelop.Projects.Project)sender;
			var projectId = GetProjectId (project);
			OnProjectReloaded (LoadProject (project)); 
		}

		#endregion
	
	}

//	static class MonoDevelopWorkspaceFeatures
//	{
//		static FeaturePack pack;
//
//		public static FeaturePack Features {
//			get {
//				if (pack == null)
//					Interlocked.CompareExchange (ref pack, ComputePack (), null);
//				return pack;
//			}
//		}
//
//		static FeaturePack ComputePack ()
//		{
//			var assemblies = new List<Assembly> ();
//			var workspaceCoreAssembly = typeof(Workspace).Assembly;
//			assemblies.Add (workspaceCoreAssembly);
//
//			LoadAssembly (assemblies, "Microsoft.CodeAnalysis.CSharp.Workspaces");
//			//LoadAssembly (assemblies, "Microsoft.CodeAnalysis.VisualBasic.Workspaces");
//
//			var catalogs = assemblies.Select (a => new System.ComponentModel.Composition.Hosting.AssemblyCatalog (a));
//
//			return new MefExportPack (catalogs);
//		}
//
//		static void LoadAssembly (List<Assembly> assemblies, string assemblyName)
//		{
//			try {
//				var loadedAssembly = Assembly.Load (assemblyName);
//				assemblies.Add (loadedAssembly);
//			} catch (Exception e) {
//				LoggingService.LogWarning ("Couldn't load assembly:" + assemblyName, e);
//			}
//		}
//	}

}