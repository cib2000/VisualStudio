﻿using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using GitHub.Services;
using GitHub.Extensions;
using GitHub.Primitives;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using LibGit2Sharp;

namespace GitHub.App.Services
{
    [Export(typeof(IGitHubContextService))]
    public class GitHubContextService : IGitHubContextService
    {
        readonly IServiceProvider serviceProvider;
        readonly IGitService gitService;

        // USERID_REGEX = /[a-z0-9][a-z0-9\-\_]*/i
        const string owner = "(?<owner>[a-zA-Z0-9][a-zA-Z0-9-_]*)";

        // REPO_REGEX = /(?:\w|\.|\-)+/i
        // This supports "_" for legacy superfans with logins that still contain "_".
        const string repo = @"(?<repo>(?:\w|\.|\-)+)";

        //BRANCH_REGEX = /[^\/]+(\/[^\/]+)?/
        const string branch = @"(?<branch>[^./ ~^:?*\[\\][^/ ~^:?*\[\\]*(/[^./ ~^:?*\[\\][^/ ~^:?*\[\\]*)*)";

        const string pull = "(?<pull>[0-9]+)";

        const string issue = "(?<issue>[0-9]+)";

        static readonly string tree = $"^{repo}/(?<tree>[^ ]+)";
        static readonly string blobName = $"^{repo}/(?<blobName>[^ /]+)";

        static readonly Regex windowTitleRepositoryRegex = new Regex($"^(GitHub - )?{owner}/{repo}(: .*)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBranchRegex = new Regex($"^(GitHub - )?{owner}/{repo} at {branch} ", RegexOptions.Compiled);
        static readonly Regex windowTitlePullRequestRegex = new Regex($" · Pull Request #{pull} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleIssueRegex = new Regex($" · Issue #{issue} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBlobRegex = new Regex($"{blobName} at {branch} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleTreeRegex = new Regex($"{tree} at {branch} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBranchesRegex = new Regex($"Branches · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);

        static readonly Regex urlLineRegex = new Regex($"#L(?<line>[0-9]+)(-L(?<lineEnd>[0-9]+))?$", RegexOptions.Compiled);
        static readonly Regex urlBlobRegex = new Regex($"blob/(?<treeish>[^/]+(/[^/]+)*)/(?<blobName>[^/#]+)", RegexOptions.Compiled);

        static readonly Regex treeishCommitRegex = new Regex($"(?<commit>[a-z0-9]{{40}})(/(?<tree>.+))?", RegexOptions.Compiled);
        static readonly Regex treeishBranchRegex = new Regex($"(?<branch>master)(/(?<tree>.+))?", RegexOptions.Compiled);

        [ImportingConstructor]
        public GitHubContextService([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider, IGitService gitService)
        {
            this.serviceProvider = serviceProvider;
            this.gitService = gitService;
        }

        public GitHubContext FindContextFromClipboard()
        {
            var text = Clipboard.GetText(TextDataFormat.Text);
            return FindContextFromUrl(text);
        }

        public GitHubContext FindContextFromUrl(string url)
        {
            var uri = new UriString(url);
            if (!uri.IsValidUri)
            {
                return null;
            }

            if (!uri.IsHypertextTransferProtocol)
            {
                return null;
            }

            var context = new GitHubContext
            {
                Host = uri.Host,
                Owner = uri.Owner,
                RepositoryName = uri.RepositoryName,
            };

            var repositoryPrefix = uri.ToRepositoryUrl().ToString() + "/";
            if (!url.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return context;
            }

            var subpath = url.Substring(repositoryPrefix.Length);

            (context.Line, context.LineEnd) = FindLine(subpath);

            context.PullRequest = FindPullRequest(url);

            var match = urlBlobRegex.Match(subpath);
            if (match.Success)
            {
                context.TreeishPath = match.Groups["treeish"].Value;
                context.BlobName = match.Groups["blobName"].Value;
                return context;
            }

            return context;
        }

        public GitHubContext FindContextFromBrowser()
        {
            return
                FindWindowTitlesForClass("Chrome_WidgetWin_1")              // Chrome
                .Concat(FindWindowTitlesForClass("MozillaWindowClass"))     // Firefox
                .Select(FindContextFromWindowTitle).Where(x => x != null)
                .FirstOrDefault();
        }

        public IEnumerable<string> FindWindowTitlesForClass(string className = "MozillaWindowClass")
        {
            IntPtr handleWin = IntPtr.Zero;
            while (IntPtr.Zero != (handleWin = User32.FindWindowEx(IntPtr.Zero, handleWin, className, IntPtr.Zero)))
            {
                // Allocate correct string length first
                int length = User32.GetWindowTextLength(handleWin);
                if (length == 0)
                {
                    continue;
                }

                var titleBuilder = new StringBuilder(length + 1);
                User32.GetWindowText(handleWin, titleBuilder, titleBuilder.Capacity);
                yield return titleBuilder.ToString();
            }
        }

        public Uri ToRepositoryUrl(GitHubContext context)
        {
            var builder = new UriBuilder("https", context.Host ?? "github.com");
            builder.Path = $"{context.Owner}/{context.RepositoryName}";
            return builder.Uri;
        }

        public GitHubContext FindContextFromWindowTitle(string windowTitle)
        {
            var match = windowTitleBlobRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                    BlobName = match.Groups["blobName"].Value
                };
            }

            match = windowTitleTreeRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                    TreeishPath = $"{match.Groups["branch"].Value}/{match.Groups["tree"].Value}"
                };
            }

            match = windowTitleRepositoryRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                };
            }

            match = windowTitleBranchRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                };
            }

            match = windowTitleBranchesRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value
                };
            }

            match = windowTitlePullRequestRegex.Match(windowTitle);
            if (match.Success)
            {
                int.TryParse(match.Groups["pull"].Value, out int pullRequest);

                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    PullRequest = pullRequest
                };
            }

            match = windowTitleIssueRegex.Match(windowTitle);
            if (match.Success)
            {
                int.TryParse(match.Groups["issue"].Value, out int issue);

                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    Issue = issue
                };
            }

            return null;
        }

        public bool TryOpenFile(string repositoryDir, GitHubContext context)
        {
            var fileName = context.BlobName;
            if (fileName == null)
            {
                return false;
            }

            string fullPath;
            var resolvedPath = ResolvePath(context);
            if (resolvedPath != null)
            {
                fullPath = Path.Combine(repositoryDir, resolvedPath);
                if (!File.Exists(fullPath))
                {
                    return false;
                }
            }
            else
            {
                // Search by filename only
                fullPath = Directory.EnumerateFiles(repositoryDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (fullPath == null)
                {
                    return false;
                }
            }

            var textView = OpenDocument(fullPath);

            var line = context.Line;
            if (line != null)
            {
                var lineEnd = context.LineEnd ?? line;

                ErrorHandler.ThrowOnFailure(textView.GetBuffer(out IVsTextLines buffer));
                buffer.GetLengthOfLine(lineEnd.Value - 1, out int lineEndLength);

                ErrorHandler.ThrowOnFailure(textView.SetSelection(line.Value - 1, 0, lineEnd.Value - 1, lineEndLength));
                ErrorHandler.ThrowOnFailure(textView.CenterLines(line.Value - 1, lineEnd.Value - line.Value + 1));
            }

            return true;
        }

        public string ResolvePath(GitHubContext context)
        {
            var treeish = context.TreeishPath;
            if (treeish == null)
            {
                return null;
            }

            var blobName = context.BlobName;
            if (blobName == null)
            {
                return null;
            }

            var match = treeishCommitRegex.Match(treeish);
            if (match.Success)
            {
                var tree = match.Groups["tree"].Value.Replace('/', '\\');
                return Path.Combine(tree, blobName);
            }

            match = treeishBranchRegex.Match(treeish);
            if (match.Success)
            {
                var tree = match.Groups["tree"].Value.Replace('/', '\\');
                return Path.Combine(tree, blobName);
            }

            return null;
        }

        public GitObject ResolveGitObject(string repositoryDir, GitHubContext context)
        {
            Guard.ArgumentNotNull(repositoryDir, nameof(repositoryDir));
            Guard.ArgumentNotNull(context, nameof(context));

            using (var repository = gitService.GetRepository(repositoryDir))
            {
                var path = context.TreeishPath;
                if (context.BlobName != null)
                {
                    path += '/' + context.BlobName;
                }

                foreach (var treeish in ToTreeish(path))
                {
                    var gitObject = repository.Lookup(treeish);
                    if (gitObject != null)
                    {
                        return gitObject;
                    }
                }

                return null;
            }
        }

        static IEnumerable<string> ToTreeish(string treeishPath)
        {
            yield return treeishPath;

            var index = 0;
            while ((index = treeishPath.IndexOf('/', index + 1)) != -1)
            {
                var commitish = treeishPath.Substring(0, index);
                var path = treeishPath.Substring(index + 1);
                yield return $"{commitish}:{path}";
            }
        }

        IVsTextView OpenDocument(string fullPath)
        {
            var logicalView = VSConstants.LOGVIEWID.TextView_guid;
            IVsUIHierarchy hierarchy;
            uint itemID;
            IVsWindowFrame windowFrame;
            IVsTextView view;
            VsShellUtilities.OpenDocument(serviceProvider, fullPath, logicalView, out hierarchy, out itemID, out windowFrame, out view);
            return view;
        }

        static (int? lineStart, int? lineEnd) FindLine(UriString gitHubUrl)
        {
            var url = gitHubUrl.ToString();

            var match = urlLineRegex.Match(url);
            if (match.Success)
            {
                int.TryParse(match.Groups["line"].Value, out int line);

                var lineEndGroup = match.Groups["lineEnd"];
                if (string.IsNullOrEmpty(lineEndGroup.Value))
                {
                    return (line, null);
                }

                int.TryParse(lineEndGroup.Value, out int lineEnd);
                return (line, lineEnd);
            }

            return (null, null);
        }

        static int? FindPullRequest(UriString gitHubUrl)
        {
            var pullRequest = FindSubPath(gitHubUrl, "/pull/")?.Split('/').First();
            if (pullRequest == null)
            {
                return null;
            }

            if (!int.TryParse(pullRequest, out int number))
            {
                return null;
            }

            return number;
        }

        static string FindSubPath(UriString gitHubUrl, string matchPath)
        {
            var url = gitHubUrl.ToString();
            var prefix = gitHubUrl.ToRepositoryUrl() + matchPath;
            if (!url.StartsWith(prefix))
            {
                return null;
            }

            var endIndex = url.IndexOf('#');
            if (endIndex == -1)
            {
                endIndex = gitHubUrl.Length;
            }

            var path = url.Substring(prefix.Length, endIndex - prefix.Length);
            return path;
        }

        static class User32
        {
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            internal static extern int GetWindowTextLength(IntPtr hWnd);
        }
    }
}
