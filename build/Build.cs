using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.Common.Utilities.Net;
using Nuke.Utilities.Text.Yaml;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static Nuke.Common.Tools.Git.GitTasks;

[GitHubActions(
    "test",
    GitHubActionsImage.UbuntuLatest,
    AutoGenerate = false,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch },
    InvokedTargets = new[] { nameof(Slack) },
    ImportSecrets = new[] { nameof(SlackWebhook) },
    EnableGitHubToken = true)]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>();

    public Build()
    {
        YamlExtensions.DefaultDeserializerBuilder = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance);
    }

    [GitRepository]
    readonly GitRepository Repository;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    [Parameter] [Secret]
    readonly string SlackWebhook;

    readonly string DefaultIconUrl = "https://resources.jetbrains.com/storage/products/company/brand/logos/jb_beam.png";

    readonly Dictionary<string, string> ProductIconUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ReSharper"] = "https://resources.jetbrains.com/storage/products/company/brand/logos/ReSharper_icon.png",
        ["Rider"] = "https://resources.jetbrains.com/storage/products/company/brand/logos/Rider_icon.png",
        ["dotMemory"] = "https://resources.jetbrains.com/storage/products/company/brand/logos/dotMemory_icon.png",
        ["dotTrace"] = "https://resources.jetbrains.com/storage/products/company/brand/logos/dotTrace_icon.png",
        ["dotCover"] = "https://resources.jetbrains.com/storage/products/company/brand/logos/dotCover_icon.png",
    };

    AbsolutePath TipsDirectory => RootDirectory / "tips";
    AbsolutePath NewTipsDirectory => TipsDirectory / "_new";

    Target Slack => _ => _
        // .Triggers(Commit)
        .Executes(async () =>
        {
            TipsDirectory.GlobFiles("**/*").ForEach(x => Log.Information(x));
            var firstTip = NewTipsDirectory.GetDirectories().First();

            await PostSlack(firstTip);
            
            // FileSystemTasks.MoveDirectoryToDirectory(firstTip, TipsDirectory);
        });

    string CommitterName => GitHubActions.Workflow;
    string CommitterEmail => "actions@github.com";

    Target Commit => _ => _
        .OnlyWhenDynamic(() => !GitHasCleanWorkingCopy())
        .Executes(() =>
        {
            var remote = $"https://{GitHubActions.Actor}:{GitHubActions.Token}@github.com/{GitHubActions.Repository}";
            Git($"remote set-url origin {remote.DoubleQuote()}");
            Git($"config user.name {CommitterName.DoubleQuote()}");
            Git($"config user.email {CommitterEmail.DoubleQuote()}");
            Git($"add {TipsDirectory}");
            Git($"commit -m {"Update".DoubleQuote()}");
            Git($"push origin HEAD:{Repository.Branch}");
        });
    
    async Task PostSlack(AbsolutePath directory)
    {
        Log.Information(GitHubActions.Ref);
        Log.Information(GitHubActions.BaseRef);
        Log.Information(GitHubActions.HeadRef);
        
        var post = (directory / "index.yml").ReadYaml<Post>();
        var images = directory.GlobFiles("*.{gif,png}")
            .Select(x => Repository.GetGitHubDownloadUrl(x, GitHubActions.BaseRef))
            .ForEachLazy(Log.Information)
            .ToList();

        var client = new HttpClient();
        await client
            .CreateRequest(HttpMethod.Post, SlackWebhook)
            .WithJsonContent(new
            {
                attachments = new[]
                {
                    new
                    {
                        fallback = post.Tweet ?? post.Text,
                        text = post.Tweet ?? post.Text,
                        author_name = post.Title,
                        author_icon = post.Products is not [var product]
                            ? DefaultIconUrl
                            : ProductIconUrls[product],
                        author_link = post.ReadMore,
                        author_subname = ProductIconUrls.Keys
                            .Aggregate(post.Products.Join(" & "), (c, p) => c.ReplaceRegex(p, _ => p, RegexOptions.IgnoreCase)),
                        image_url = images.First(),
                        footer = post.Hashtags.Concat(post.Technology, post.Topic)
                            .Select(x => $"#{x.ToLowerInvariant()}").JoinSpace()
                    }
                }
            })
            .GetResponseAsync();
    }

    public record Post
    {
        public string Title;
        public string[] Products;
        public string Scheduled;
        public string Version;
        public string OS;
        public string Technology;
        public string Topic;
        public bool Fun;
        public string[] Hashtags;
        public string ReadMore;
        public string Text;
        public string Tweet;
        public string ImageUrl;
    }
}
