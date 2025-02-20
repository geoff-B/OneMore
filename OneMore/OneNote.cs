﻿//************************************************************************************************
// Copyright © 2016 Steven M Cohn.  All rights reserved.
//************************************************************************************************                

#pragma warning disable S3881 // IDisposable should be implemented correctly

namespace River.OneMoreAddIn
{
	using Microsoft.Office.Interop.OneNote;
	using River.OneMoreAddIn.Models;
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Runtime.InteropServices;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Xml;
	using System.Xml.Linq;
	using System.Xml.Schema;
	using Forms = System.Windows.Forms;
	using Resx = River.OneMoreAddIn.Properties.Resources;


	/// <summary>
	/// Wraps the OneNote interop API
	/// </summary>
	/// <see cref="https://docs.microsoft.com/en-us/office/client-developer/onenote/application-interface-onenote"/>
	internal class OneNote : IDisposable
	{
		public enum ExportFormat
		{
			EMF = PublishFormat.pfEMF,
			HTML = PublishFormat.pfHTML,
			MHTML = PublishFormat.pfMHTML,
			OneNote = PublishFormat.pfOneNote,
			OneNote2007 = PublishFormat.pfOneNote2007,
			OneNotePackage = PublishFormat.pfOneNotePackage,
			PDF = PublishFormat.pfPDF,
			Word = PublishFormat.pfWord,
			XPS = PublishFormat.pfXPS,
			XML = 1000
		}

		public enum PageDetail
		{
			All = PageInfo.piAll,
			Basic = PageInfo.piBasic,
			BinaryData = PageInfo.piBinaryData,
			BinaryDataFileType = PageInfo.piBinaryDataFileType,
			BinaryDataSelection = PageInfo.piBinaryDataSelection,
			FileType = PageInfo.piFileType,
			Selection = PageInfo.piSelection,
			SelectionFileType = PageInfo.piSelectionFileType
		}

		public enum Scope
		{
			Children = HierarchyScope.hsChildren,
			Notebooks = HierarchyScope.hsNotebooks,
			Pages = HierarchyScope.hsPages,
			Sections = HierarchyScope.hsSections,
			Self = HierarchyScope.hsSelf
		}

		public class HyperlinkInfo
		{
			public string PageID;       // pageID
			public string SectionID;    // sectionID
			public string HyperID;      // hyperlink section-id or page-id
			public string Name;         // section or page name
			public string Path;         // relative path within current scope (section, notebook)
			public string FullPath;     // full notebook-rooted path
			public string Uri;          // onenote:blah hyperlink
		}

		public const string Prefix = "one";

		private const int MaxInclusiveHResult = -2146231999;

		private IApplication onenote;
		private readonly ILogger logger;
		private bool disposed;

		private Regex pageEx;


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Constructors...

		/// <summary>
		/// Initialize a new wrapper
		/// </summary>
		public OneNote()
		{
			onenote = ApplicationFactory.CreateApplication();
			logger = Logger.Current;
		}


		/// <summary>
		/// Initialize a new wrapper and return the current page
		/// </summary>
		/// <param name="page">The current Page</param>
		/// <param name="ns">The namespace of the current page</param>
		/// <param name="detail">The desired verbosity of the XML</param>
		public OneNote(out Page page, out XNamespace ns, PageDetail detail = PageDetail.Selection)
			: this()
		{
			page = GetPage(detail);
			ns = page.Namespace;
		}


		#region Lifecycle
		protected virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				if (disposing)
				{
					onenote = null;
				}

				disposed = true;
			}
		}


		public void Dispose()
		{
			Dispose(disposing: true);
			// DO NOT call this otherwise OneNote will not shutdown properly
			//GC.SuppressFinalize(this);
		}
		#endregion Lifecycle


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Properties

		/// <summary>
		/// Gets the currently viewed page ID
		/// </summary>
		public string CurrentPageId => onenote.Windows.CurrentWindow?.CurrentPageId;


		/// <summary>
		/// Gets the currently viewed section ID
		/// </summary>
		public string CurrentSectionId => onenote.Windows.CurrentWindow?.CurrentSectionId;


		/// <summary>
		/// Gets the currently viewed notebook ID
		/// </summary>
		public string CurrentNotebookId => onenote.Windows.CurrentWindow?.CurrentNotebookId;


		/// <summary>
		/// Gets the Win32 Window associated with the current window's handle
		/// </summary>
		public Forms.IWin32Window Window => Forms.Control.FromHandle(WindowHandle);


		/// <summary>
		/// Gets the handle of the current window
		/// </summary>
		public IntPtr WindowHandle => (IntPtr)onenote.Windows.CurrentWindow.WindowHandle;


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

		/// <summary>
		/// Invoke an action with retry
		/// </summary>
		/// <param name="work">The action to invoke</param>
		public async Task InvokeWithRetry(Action work)
		{
			try
			{
				int retries = 0;
				while (retries < 3)
				{
					try
					{
						work();

						if (retries > 0)
						{
							logger.WriteLine($"completed successfully after {retries} retries");
						}

						retries = int.MaxValue;
					}
					catch (COMException exc) when ((uint)exc.ErrorCode == ErrorCodes.hrCOMBusy)
					{
						retries++;
						var ms = 250 * retries;

						logger.WriteLine($"OneNote is busy, retyring in {ms}ms", exc);
						await Task.Delay(ms);
					}
				}
			}
			catch (Exception exc)
			{
				logger.WriteLine("error invoking action", exc);
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Hyperlink map...

		/// <summary>
		/// Creates a map of pages where the key is built from the page-id of an internal
		/// onenote: hyperlink and the value is a HyperlinkInfo item
		/// </summary>
		/// <param name="scope">Pages in section, Sections in notebook, or all Notebooks</param>
		/// <param name="countCallback">Called exactly once to report the total count of pages to map</param>
		/// <param name="stepCallback">Called for each page that is mapped to report progress</param>
		/// <returns>
		/// A Dictionary with page IDs as keys as values are HyperlinkInfo items
		/// </returns>
		/// <remarks>
		/// There's no direct way to map onenote:http URIs to page IDs so this creates a cache
		/// of all pages in the specified scope with their URIs as keys and pageIDs as values
		/// </remarks>
		public async Task<Dictionary<string, HyperlinkInfo>> BuildHyperlinkMap(
			Scope scope,
			CancellationToken token,
			Func<int, Task> countCallback = null,
			Func<Task> stepCallback = null)
		{
			var hyperlinks = new Dictionary<string, HyperlinkInfo>();

			XElement container;
			if (scope == Scope.Notebooks)
			{
				container = GetNotebooks(Scope.Pages);
			}
			else if (scope == Scope.Sections || scope == Scope.Pages)
			{
				// get the notebook even if scope if Pages so we can infer the full path
				container = GetNotebook(Scope.Pages);
			}
			else
			{
				await Task.FromResult(hyperlinks);
				return hyperlinks;
			}

			// ignore the recycle bin
			container.Elements()
				.Where(e => e.Attributes().Any(a => a.Name == "isRecycleBin"))
				.Remove();

			if (token.IsCancellationRequested)
			{
				await Task.FromResult(hyperlinks);
				return hyperlinks;
			}

			// get root path and trim down to intended scope

			var ns = GetNamespace(container);
			string rootPath = string.Empty;

			if (scope == Scope.Pages)
			{
				var section = container.Descendants(ns + "Section")
					.FirstOrDefault(e => e.Attribute("isCurrentlyViewed")?.Value == "true");

				if (section != null)
				{
					var p = section.Parent;
					while (p != null)
					{
						var a = p.Attribute("name");
						if (a != null && !string.IsNullOrEmpty(a.Value))
						{
							rootPath = rootPath.Length == 0 ? a.Value : $"{a.Value}/{rootPath}";
						}

						p = p.Parent;
					}

					container = section;
				}
			}
			else if (scope != Scope.Notebooks) // <one:Notebooks> doesn't have a name
			{
				rootPath = container.Attribute("name").Value;
			}

			if (token.IsCancellationRequested)
			{
				return hyperlinks;
			}

			// count pages so we can update countCallback and continue
			var total = container.Descendants(ns + "Page").Count();
			if (total > 0)
			{
				if (countCallback != null)
					await countCallback(total);

				pageEx = new Regex(@"page-id=({[^}]+?})");

				await BuildHyperlinkMap(hyperlinks, container, rootPath, null, token, stepCallback);
			}

			await Task.FromResult(hyperlinks);
			return hyperlinks;
		}


		private async Task BuildHyperlinkMap(
			Dictionary<string, HyperlinkInfo> hyperlinks,
			XElement root, string fullPath, string path,
			CancellationToken token, Func<Task> stepCallback)
		{
			if (root.Name.LocalName == "Section")
			{
				if (string.IsNullOrEmpty(path))
				{
					path = root.Attribute("name").Value;
				}

				var full = $"{fullPath}/{path}";

				foreach (var element in root.Elements())
				{
					if (token.IsCancellationRequested)
					{
						return;
					}

					var ID = element.Attribute("ID").Value;
					var name = element.Attribute("name").Value;
					var link = GetHyperlink(ID, string.Empty);
					var match = pageEx.Match(link);
					if (match.Success)
					{
						//logger.WriteLine($"MAP path:{path} fullpath:{full} name:{name}");

						hyperlinks.Add(match.Groups[1].Value,
							new HyperlinkInfo
							{
								PageID = ID,
								SectionID = null,
								HyperID = match.Groups[1].Value,
								Name = name,
								Path = path,
								FullPath = full,
								Uri = link
							});
					}

					if (stepCallback != null)
						await stepCallback();
				}
			}
			else // SectionGroup or Notebook
			{
				foreach (var element in root.Elements())
				{
					if (element.Name.LocalName == "Notebooks" ||
						element.Name.LocalName == "UnfiledNotes")
					{
						//logger.WriteLine($"MAP skip {element.Name.LocalName} element");
						continue;
					}

					var ea = element.Attribute("name");
					if (ea == null)
						continue;

					//logger.WriteLine($"MAP root:{root.Name.LocalName}={root.Attribute("name")?.Value} " +
					//	$"element:{element.Name.LocalName}={ea?.Value} " +
					//	$"path:{path}");

					var p = string.IsNullOrEmpty(path) ? ea?.Value : $"{path}/{ea?.Value}";

					await BuildHyperlinkMap(
						hyperlinks, element,
						fullPath,
						p,
						token, stepCallback);

					if (token.IsCancellationRequested)
					{
						return;
					}
				}
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Create...

		/// <summary>
		/// Creates a new page in the specified section
		/// </summary>
		/// <param name="sectionId">The section to contain the new page</param>
		/// <param name="pageId">The ID of the new page</param>
		public void CreatePage(string sectionId, out string pageId)
		{
			onenote.CreateNewPage(sectionId, out pageId);
		}


		/// <summary>
		/// Create a new section in the current notebook immediately after the open section
		/// </summary>
		/// <param name="name">The name of the new section</param>
		/// <returns>The Section element</returns>
		public XElement CreateSection(string name)
		{
			// find the current section in this notebook (may be in section group)
			var notebook = GetNotebook();
			var ns = GetNamespace(notebook);
			var current = notebook.Descendants(ns + "Section")
				.FirstOrDefault(e => e.Attribute("isCurrentlyViewed")?.Value == "true");

			XElement parent;
			var sectionIds = new List<string>();

			var section = new XElement(ns + "Section", new XAttribute("name", name));

			if (current == null)
			{
				// add first section to notebook
				notebook.Add(section);
				parent = notebook;
			}
			else
			{
				// get the parent of the current section (may be the notebook or a section group)
				// so we will then only update that parent rather than the entire hierarchy
				parent = current.Parent;

				// collect all section IDs for comparison
				sectionIds = parent.Elements(ns + "Section")
					.Where(e => e.Attribute("isRecycleBin") == null &&
								e.Attribute("isInRecycleBin") == null)
					.Attributes("ID").Select(a => a.Value).ToList();

				// add the new section (won't know the ID yet)
				current.AddAfterSelf(section);
			}

			// udpate the notebook or section group
			UpdateHierarchy(parent);

			// get the parent again so we can find the new section ID
			onenote.GetHierarchy(
				parent.Attribute("ID").Value,
				HierarchyScope.hsSections, out var xml, XMLSchema.xs2013);

			parent = XElement.Parse(xml);

			// compare the new parent with old section IDs to find the new ID
			section = parent.Elements(ns + "Section")
				.Where(e => e.Attribute("isRecycleBin") == null &&
							e.Attribute("isInRecycleBin") == null)
				.FirstOrDefault(e => !sectionIds.Contains(e.Attribute("ID").Value));

			return section;
		}


		public XElement CreateSection(XElement section, XElement parent)
		{
			return section;
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Get...

		/// <summary>
		/// Get the known paths used by OneNote; this is for diagnostic logging
		/// </summary>
		/// <returns></returns>
		public (string backupFolder, string defaultFolder, string unfiledFolder) GetFolders()
		{
			onenote.GetSpecialLocation(SpecialLocation.slBackUpFolder, out var backupFolder);
			onenote.GetSpecialLocation(SpecialLocation.slDefaultNotebookFolder, out var defaultFolder);
			onenote.GetSpecialLocation(SpecialLocation.slUnfiledNotesSection, out var unfiledFolder);
			return (backupFolder, defaultFolder, unfiledFolder);
		}


		/// <summary>
		/// Gets a onenote:hyperline to an object on the specified page
		/// </summary>
		/// <param name="pageId">The ID of a page</param>
		/// <param name="objectId">
		/// The ID of an object on the page or string.Empty to link to the page itself
		/// </param>
		/// <returns></returns>
		public string GetHyperlink(string pageId, string objectId)
		{
			onenote.GetHyperlinkToObject(pageId, objectId, out var hyperlink);
			return hyperlink;
		}


		/// <summary>
		/// Gets the OneNote namespace for the given element
		/// </summary>
		/// <param name="element"></param>
		/// <returns></returns>
		public XNamespace GetNamespace(XElement element)
		{
			return element.GetNamespaceOfPrefix(Prefix);
		}


		/// <summary>
		/// Gets the current notebook with a hierarchy of sections
		/// </summary>
		/// <returns>A Notebook element with Section children</returns>
		public XElement GetNotebook(Scope scope = Scope.Sections)
		{
			return GetNotebook(CurrentNotebookId, scope);
		}


		/// <summary>
		/// Get the spcified notebook with a hierarchy of sections
		/// </summary>
		/// <param name="id">The ID of the notebook</param>
		/// <returns>A Notebook element with Section children</returns>
		public XElement GetNotebook(string id, Scope scope = Scope.Sections)
		{
			if (!string.IsNullOrEmpty(id))
			{
				onenote.GetHierarchy(id, (HierarchyScope)scope, out var xml, XMLSchema.xs2013);
				if (!string.IsNullOrEmpty(xml))
				{
					return XElement.Parse(xml);
				}
			}

			return null;
		}


		/// <summary>
		/// Gets a root note containing Notebook elements
		/// </summary>
		/// <returns>A Notebooks element with Notebook children</returns>
		public XElement GetNotebooks(Scope scope = Scope.Notebooks)
		{
			// find the ID of the current notebook
			onenote.GetHierarchy(
				string.Empty, (HierarchyScope)scope, out var xml, XMLSchema.xs2013);

			if (!string.IsNullOrEmpty(xml))
			{
				return XElement.Parse(xml);
			}

			return null;
		}


		/// <summary>
		/// Gets the current page.
		/// </summary>
		/// <param name="detail">The desired verbosity of the XML</param>
		/// <returns>A Page containing the root XML of the page</returns>
		public Page GetPage(PageDetail detail = PageDetail.Selection)
		{
			return GetPage(CurrentPageId, detail);
		}


		/// <summary>
		/// Gets the specified page.
		/// </summary>
		/// <param name="pageId">The unique ID of the page</param>
		/// <param name="detail">The desired verbosity of the XML</param>
		/// <returns>A Page containing the root XML of the page</returns>
		public Page GetPage(string pageId, PageDetail detail = PageDetail.All)
		{
			if (string.IsNullOrEmpty(pageId))
			{
				return null;
			}

			onenote.GetPageContent(pageId, out var xml, (PageInfo)detail, XMLSchema.xs2013);
			if (!string.IsNullOrEmpty(xml))
			{
				return new Page(XElement.Parse(xml));
			}

			return null;
		}


		/// <summary>
		/// Gets the raw XML of the specified page.
		/// </summary>
		/// <param name="pageId">The unique ID of the page</param>
		/// <param name="detail">The desired verbosity of the XML</param>
		/// <returns>A string specifying the root XML of the page</returns>
		public string GetPageXml(string pageId, PageDetail detail = PageDetail.Basic)
		{
			if (string.IsNullOrEmpty(pageId))
			{
				return null;
			}

			onenote.GetPageContent(pageId, out var xml, (PageInfo)detail, XMLSchema.xs2013);
			return xml;
		}


		/// <summary>
		/// Gets the name, file path, and OneNote hyperlink to the current page;
		/// used to build up Favorites
		/// </summary>
		/// <returns></returns>
		public (string Name, string Path, string Link) GetPageInfo(string pageId = null)
		{
			var page = GetPage(pageId ?? CurrentPageId, PageDetail.Basic);
			if (page == null)
			{
				return (null, null, null);
			}

			// name
			var name = page.Root.Attribute("name")?.Value;

			// path
			string path = null;
			var sectionID = GetParent(page.PageId) ?? CurrentSectionId;
			var section = GetSection(sectionID);
			if (section != null)
			{
				var uri = section.Attribute("path")?.Value;
				if (!string.IsNullOrEmpty(uri))
				{
					path = "/" + Path.Combine(
						Path.GetFileName(Path.GetDirectoryName(uri)),
						Path.GetFileNameWithoutExtension(uri),
						name
						).Replace("\\", "/");
				}
			}

			// link
			string link = GetHyperlink(page.PageId, string.Empty);

			return (name, path, link);
		}


		/// <summary>
		/// Gets the ID of the parent hierachy object that owns the specified object; used when
		/// copying/moving pages from section to section
		/// </summary>
		/// <param name="objectId">The ID of the object whose parent you want to find</param>
		/// <returns>The ID of the parent or null if not found (e.g. parent of a notebook)</returns>
		public string GetParent(string objectId)
		{
			try
			{
				onenote.GetHierarchyParent(objectId, out var parentId);
				return parentId;
			}
			catch
			{
				return null;
			}
		}


		/// <summary>
		/// Gest the current section and its child page hierarchy
		/// </summary>
		/// <returns>A Section element with Page children</returns>
		public XElement GetSection()
		{
			return GetSection(CurrentSectionId);
		}


		/// <summary>
		/// Gest the specified section and its child page hierarchy
		/// </summary>
		/// <returns>A Section element with Page children</returns>
		public XElement GetSection(string id)
		{
			if (!string.IsNullOrEmpty(id))
			{
				onenote.GetHierarchy(id, HierarchyScope.hsPages, out var xml, XMLSchema.xs2013);
				if (!string.IsNullOrEmpty(xml))
				{
					return XElement.Parse(xml);
				}
			}

			return null;
		}


		/// <summary>
		/// Gets the name, file path, and OneNote hyperlink to the current section;
		/// used to build up Favorites
		/// </summary>
		/// <returns></returns>
		public (string Name, string Path, string Link) GetSectionInfo()
		{
			var section = GetSection();
			if (section == null)
			{
				return (null, null, null);
			}

			// name
			var name = section.Attribute("name")?.Value;

			// path
			string path = section.Attribute("path")?.Value;
			if (!string.IsNullOrEmpty(path))
			{
				path = "/" + Path.Combine(
					Path.GetFileName(Path.GetDirectoryName(path)),
					Path.GetFileNameWithoutExtension(path)
					).Replace("\\", "/");
			}

			// link
			string link = null;
			var sectionId = section.Attribute("ID")?.Value;
			if (sectionId != null)
			{
				link = GetHyperlink(sectionId, string.Empty);
			}

			return (name, path, link);
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Update...

		/// <summary>
		/// Deletes the given object from the specified page
		/// </summary>
		/// <param name="pageId">The ID of the page to modify</param>
		/// <param name="objectId">The ID of the object to remove from the page</param>
		public void DeleteContent(string pageId, string objectId)
		{
			try
			{
				onenote.DeletePageContent(pageId, objectId);
			}
			catch (Exception exc)
			{
				logger.WriteLine($"error deleting page object {objectId}", exc);
			}
		}


		/// <summary>
		/// Deletes the given object(s) from the hierarchy; used for merging
		/// </summary>
		/// <param name="element"></param>
		public void DeleteHierarchy(string objectId)
		{
			try
			{
				onenote.DeleteHierarchy(objectId);
			}
			catch (Exception exc)
			{
				logger.WriteLine($"error deleting hierarchy object {objectId}", exc);
			}
		}


		/// <summary>
		/// Sync updates to storage
		/// </summary>
		/// <param name="id">
		/// The ID of the notebook to sync or the current notebook by default
		/// </param>
		public async Task Sync(string id = null)
		{
			await InvokeWithRetry(() =>
			{
				onenote.SyncHierarchy(id ?? CurrentNotebookId);
			});
		}


		/// <summary>
		/// Update the current page.
		/// </summary>
		/// <param name="page">A Page</param>
		public async Task Update(Page page)
		{
			await Update(page.Root);
		}


		/// <summary>
		/// Updates the given content, with a unique ID, on the current page.
		/// </summary>
		/// <param name="element">A page or element within a page with a unique objectID</param>
		public async Task Update(XElement element)
		{
			if (element.Name.LocalName == "Page")
			{
				if (!ValidateSchema(element))
				{
					return;
				}
			}

			var xml = element.ToString(SaveOptions.DisableFormatting);

			await InvokeWithRetry(() =>
			{
				onenote.UpdatePageContent(xml, DateTime.MinValue, XMLSchema.xs2013, true);
			});
		}


		private bool ValidateSchema(XElement root)
		{
			var document = new XDocument(root);
			var ns = GetNamespace(root);

			var schemas = new XmlSchemaSet();
			schemas.Add(ns.ToString(), 
				XmlReader.Create(new StringReader(Resx._0336_OneNoteApplication_2013)));

			bool valid = true;
			document.Validate(schemas, (o, e) =>
			{
				if (o is XAttribute attribute && 
					e.Exception.InnerException?.HResult == MaxInclusiveHResult)
				{
					var exp = attribute.Value.IndexOf('E');
					if (exp > 0)
					{
						var dot = attribute.Value.IndexOf('.');
						if (dot < exp - 1)
						{
							var correction = attribute.Value.Substring(0, dot + 2);
							logger.WriteLine($"corrected attribute [{o}] -> [{correction}]");
							attribute.Value = correction;
							return;
						}
					}
				}

				logger.WriteLine($"schema error [{o}] ({o.GetType().FullName})");
				logger.WriteLine(e.Exception);
				valid = false;
			});

			return valid;
		}


		/// <summary>
		/// Update the hierarchy info with the given XML; used for sorting
		/// </summary>
		/// <param name="element"></param>
		public void UpdateHierarchy(XElement element)
		{
			string xml = element.ToString(SaveOptions.DisableFormatting);

			try
			{
				onenote.UpdateHierarchy(xml);
			}
			catch (Exception exc)
			{
				logger.WriteLine("error updating hierarchy", exc);
				logger.WriteLine(element);
				logger.WriteLine();
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// QuickFiling...

		/// <summary>
		/// A callback delegate for consumers who want to know the selected item in the
		/// QuickFiling dialog.
		/// </summary>
		/// <param name="nodeId"></param>
		public delegate Task SelectLocationCallback(string nodeId);


		/// <summary>
		/// Presents the QuickFiling dialog to the user and invokes the given callback
		/// when completed.
		/// </summary>
		/// <param name="title"></param>
		/// <param name="description"></param>
		/// <param name="scope"></param>
		/// <param name="callback"></param>
		public void SelectLocation(
			string title, string description, Scope scope, SelectLocationCallback callback)
		{
			var dialog = onenote.QuickFiling();
			dialog.Title = title;
			dialog.Description = description;
			dialog.ParentWindowHandle = onenote.Windows.CurrentWindow.WindowHandle;

			switch (scope)
			{
				case Scope.Notebooks:
					dialog.TreeDepth = HierarchyElement.heNotebooks;
					break;
				case Scope.Sections:
					dialog.TreeDepth = HierarchyElement.heSections;
					break;
				case Scope.Pages:
					dialog.TreeDepth = HierarchyElement.hePages;
					break;
			}

			dialog.AddButton(
				Resx.OK, HierarchyElement.heSections, HierarchyElement.heSections, false);

			dialog.Run(new FilingCallback(callback));
		}


		private class FilingCallback : IQuickFilingDialogCallback
		{
			private readonly SelectLocationCallback userCallback;

			public FilingCallback(SelectLocationCallback usercb)
			{
				userCallback = usercb;
			}

			public void OnDialogClosed(IQuickFilingDialog dialog)
			{
				userCallback(dialog.SelectedItem);
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Utilities...

		/// <summary>
		/// Exports the specified page to a file using the given format
		/// </summary>
		/// <param name="pageId">The page ID</param>
		/// <param name="path">The output file path</param>
		/// <param name="format">The format</param>
		public bool Export(string pageId, string path, ExportFormat format)
		{
			try
			{
				onenote.Publish(pageId, path, (PublishFormat)format, string.Empty);
				return true;
			}
			catch (Exception exc)
			{
				logger.WriteLine($"cannot publish page {pageId}", exc);
				return false;
			}
		}


		/// <summary>
		/// Imports the specified file under the current section.
		/// </summary>
		/// <param name="path">The path to a .one file</param>
		/// <returns>The ID of the new hierarchy object (pageId)</returns>
		public async Task<string> Import(string path)
		{
			var start = GetSection();

			// Opening a .one file places its content in the transient OpenSections area
			// with its own notebook structure; need to dive down to find the page...
			//
			// OpenHierarchy, followed by SyncHierarchy should load the .one file and then
			// GetSection would load the hierarchy down to the page level. But the one:Section
			// has an attribute areAllPagesAvailable=false which means the page isn't loaded...
			// Instead use MergeSections to force loading the pages and merge into current section

			string openSectionId = null;

			await InvokeWithRetry(() =>
			{
				onenote.OpenHierarchy(path, null, out openSectionId, CreateFileType.cftSection);
			});

			if (string.IsNullOrEmpty(openSectionId))
			{
				return null;
			}

			await InvokeWithRetry(() =>
			{
				onenote.MergeSections(openSectionId, CurrentSectionId);
			});

			var section = GetSection();
			var ns = GetNamespace(section);

			// determine newly added pageId by comparing new section against what we started with
			var pageId = section.Descendants(ns + "Page")
				.Select(e => e.Attribute("ID").Value)
				.Except(start.Descendants(ns + "Page").Select(e => e.Attribute("ID").Value))
				.FirstOrDefault();

			return pageId;
		}


		/// <summary>
		/// Forces OneNote to jump to the specified object, onenote Uri, or Web Uri
		/// </summary>
		/// <param name="uri">A pageId, sectionId, notebookId, onenote:URL, or Web URL</param>
		public async Task NavigateTo(string uri)
		{
			if (uri.StartsWith("onenote:") || uri.StartsWith("http"))
			{
				await InvokeWithRetry(() =>
				{
					onenote.NavigateToUrl(uri);
				});
			}
			else
			{
				await InvokeWithRetry(() =>
				{
			// must be an ID
			onenote.NavigateTo(uri);
				});
			}
		}


		/// <summary>
		/// Search pages under the specified hierarchy node using the given query.
		/// </summary>
		/// <param name="nodeId"></param>
		/// <param name="query"></param>
		/// <returns></returns>
		public string Search(string nodeId, string query)
		{
			onenote.FindPages(nodeId, query, out var xml, false, false, XMLSchema.xs2013);
			return xml;
		}


		/// <summary>
		/// Search the hierarchy node for the specified meta tag
		/// </summary>
		/// <param name="nodeId">The root node: notebook, section, or page</param>
		/// <param name="name">The search string, meta key name</param>
		/// <returns>A hierarchy XML starting at the given node.</returns>
		public async Task<XElement> SearchMeta(string nodeId, string name)
		{
			string xml = null;

			await InvokeWithRetry(() =>
			{
				onenote.FindMeta(nodeId, name, out xml, false, XMLSchema.xs2013);
			});

			return XElement.Parse(xml);
		}


		/// <summary>
		/// Special helper for DiagnosticsCommand
		/// </summary>
		/// <param name="builder"></param>
		public void ReportWindowDiagnostics(StringBuilder builder)
		{
			var win = onenote.Windows.CurrentWindow;

			builder.AppendLine($"CurrentNotebookId: {win.CurrentNotebookId}");
			builder.AppendLine($"CurrentPageId....: {win.CurrentPageId}");
			builder.AppendLine($"CurrentSectionId.: {win.CurrentSectionId}");
			builder.AppendLine($"CurrentSecGrpId..: {win.CurrentSectionGroupId}");
			builder.AppendLine($"DockedLocation...: {win.DockedLocation}");
			builder.AppendLine($"IsFullPageView...: {win.FullPageView}");
			builder.AppendLine($"IsSideNote.......: {win.SideNote}");
			builder.AppendLine();

			builder.AppendLine($"Windows ({onenote.Windows.Count})");

			var e = onenote.Windows.GetEnumerator();
			while (e.MoveNext())
			{
				var window = e.Current as Window;

				var threadId = Native.GetWindowThreadProcessId(
					(IntPtr)window.WindowHandle,
					out var processId);

				builder.Append($"- window [processId:{processId}, threadId:{threadId}]");
				builder.Append($" handle:{window.WindowHandle:x} active:{window.Active}");

				if (window.WindowHandle == onenote.Windows.CurrentWindow.WindowHandle)
				{
					builder.AppendLine(" (current)");
				}
			}
		}
	}
}
