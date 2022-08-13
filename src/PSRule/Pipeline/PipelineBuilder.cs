// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Reflection;
using PSRule.Configuration;
using PSRule.Definitions;
using PSRule.Definitions.Baselines;
using PSRule.Pipeline.Output;
using PSRule.Resources;
using PSRule.Runtime;

namespace PSRule.Pipeline
{
    /// <summary>
    /// A helper to create a PowerShell-based pipeline for running PSRule.
    /// </summary>
    public static class PipelineBuilder
    {
        public static IInvokePipelineBuilder Assert(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new AssertPipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IInvokePipelineBuilder Invoke(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new InvokeRulePipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IInvokePipelineBuilder Test(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new TestPipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IGetPipelineBuilder Get(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new GetRulePipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IHelpPipelineBuilder GetHelp(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new GetRuleHelpPipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static ISourcePipelineBuilder RuleSource(PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new SourcePipelineBuilder(hostContext, option);
            return pipeline;
        }

        public static IPipelineBuilder GetBaseline(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new GetBaselinePipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IPipelineBuilder ExportBaseline(Source[] source, PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new ExportBaselinePipelineBuilder(source, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }

        public static IGetTargetPipelineBuilder GetTarget(PSRuleOption option, IHostContext hostContext)
        {
            var pipeline = new GetTargetPipelineBuilder(null, hostContext);
            pipeline.Configure(option);
            return pipeline;
        }
    }

    public interface IPipelineBuilder
    {
        /// <summary>
        /// Configure the pipeline with options.
        /// </summary>
        IPipelineBuilder Configure(PSRuleOption option);

        /// <summary>
        /// Build the pipeline.
        /// </summary>
        /// <param name="writer">Optionally specify a custom writer which will handle output processing.</param>
        IPipeline Build(IPipelineWriter writer = null);
    }

    public interface IPipeline : IDisposable
    {
        /// <summary>
        /// Initalize the pipeline and results. Call this method once prior to calling Process.
        /// </summary>
        void Begin();

        /// <summary>
        /// Process an object through the pipeline. Each object will be processed by rules that apply based on pre-conditions.
        /// </summary>
        /// <param name="sourceObject">The object to process.</param>
        void Process(PSObject sourceObject);

        /// <summary>
        /// Clean up and flush pipeline results. Call this method once after processing any objects through the pipeline.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Matches PowerShell pipeline.")]
        void End();
    }

    internal abstract class PipelineBuilderBase : IPipelineBuilder
    {
        private const string ENGINE_MODULE_NAME = "PSRule";

        protected readonly PSRuleOption Option;
        protected readonly Source[] Source;
        protected readonly IHostContext HostContext;
        protected BindTargetMethod BindTargetNameHook;
        protected BindTargetMethod BindTargetTypeHook;
        protected BindTargetMethod BindFieldHook;
        protected VisitTargetObject VisitTargetObject;

        private string[] _Include;
        private Hashtable _Tag;
        private BaselineOption _Baseline;
        private string[] _Convention;
        private PathFilter _InputFilter;

        private readonly HostPipelineWriter _Output;

        private const int MIN_JSON_INDENT = 0;
        private const int MAX_JSON_INDENT = 4;

        protected PipelineBuilderBase(Source[] source, IHostContext hostContext)
        {
            Option = new PSRuleOption();
            Source = source;
            _Output = new HostPipelineWriter(hostContext, Option);
            HostContext = hostContext;
            BindTargetNameHook = PipelineHookActions.BindTargetName;
            BindTargetTypeHook = PipelineHookActions.BindTargetType;
            BindFieldHook = PipelineHookActions.BindField;
            VisitTargetObject = PipelineReceiverActions.PassThru;
        }

        public void Name(string[] name)
        {
            if (name == null || name.Length == 0)
                return;

            _Include = name;
        }

        public void Tag(Hashtable tag)
        {
            if (tag == null || tag.Count == 0)
                return;

            _Tag = tag;
        }

        public void Convention(string[] convention)
        {
            if (convention == null || convention.Length == 0)
                return;

            _Convention = convention;
        }

        public virtual IPipelineBuilder Configure(PSRuleOption option)
        {
            if (option == null)
                return this;

            Option.Binding = new BindingOption(option.Binding);
            Option.Convention = new ConventionOption(option.Convention);
            Option.Execution = new ExecutionOption(option.Execution);
            Option.Execution.LanguageMode = option.Execution.LanguageMode ?? ExecutionOption.Default.LanguageMode;
            Option.Input = new InputOption(option.Input);
            Option.Input.Format = Option.Input.Format ?? InputOption.Default.Format;
            Option.Output = new OutputOption(option.Output);
            Option.Output.Outcome = Option.Output.Outcome ?? OutputOption.Default.Outcome;
            Option.Output.Banner = Option.Output.Banner ?? OutputOption.Default.Banner;
            Option.Repository = GetRepository(Option.Repository);
            return this;
        }

        public abstract IPipeline Build(IPipelineWriter writer = null);

        /// <summary>
        /// Use a baseline, either by name or by path.
        /// </summary>
        public void UseBaseline(BaselineOption baseline)
        {
            if (baseline == null)
                return;

            _Baseline = baseline;
        }

        /// <summary>
        /// Require correct module versions for pipeline execution.
        /// </summary>
        protected bool RequireModules()
        {
            var result = true;
            if (Option.Requires.TryGetValue(ENGINE_MODULE_NAME, out var requiredVersion))
            {
                var engineVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
                if (GuardModuleVersion(ENGINE_MODULE_NAME, engineVersion, requiredVersion))
                    result = false;
            }
            for (var i = 0; Source != null && i < Source.Length; i++)
            {
                if (Source[i].Module != null && Option.Requires.TryGetValue(Source[i].Module.Name, out requiredVersion))
                {
                    if (GuardModuleVersion(Source[i].Module.Name, Source[i].Module.Version, requiredVersion))
                        result = false;
                }
            }
            return result;
        }

        /// <summary>
        /// Require sources for pipeline execution.
        /// </summary>
        protected bool RequireSources()
        {
            if (Source == null || Source.Length == 0)
            {
                PrepareWriter().WarnRulePathNotFound();
                return false;
            }
            return true;
        }

        private bool GuardModuleVersion(string moduleName, string moduleVersion, string requiredVersion)
        {
            if (!TryModuleVersion(moduleVersion, requiredVersion))
            {
                var writer = PrepareWriter();
                writer.ErrorRequiredVersionMismatch(moduleName, moduleVersion, requiredVersion);
                writer.End();
                return true;
            }
            return false;
        }

        private static bool TryModuleVersion(string moduleVersion, string requiredVersion)
        {
            return SemanticVersion.TryParseVersion(moduleVersion, out var version) &&
                SemanticVersion.TryParseConstraint(requiredVersion, out var constraint) &&
                constraint.Equals(version);
        }

        protected PipelineContext PrepareContext(BindTargetMethod bindTargetName, BindTargetMethod bindTargetType, BindTargetMethod bindField)
        {
            var unresolved = new List<ResourceRef>();
            if (_Baseline is BaselineOption.BaselineRef baselineRef)
                unresolved.Add(new BaselineRef(baselineRef.Name, OptionContext.ScopeType.Explicit));

            for (var i = 0; Source != null && i < Source.Length; i++)
            {
                if (Source[i].Module != null && Source[i].Module.Baseline != null && !unresolved.Any(u => ResourceIdEqualityComparer.IdEquals(u.Id, Source[i].Module.Baseline)))
                {
                    unresolved.Add(new BaselineRef(Source[i].Module.Baseline, OptionContext.ScopeType.Module));
                    PrepareWriter().WarnModuleManifestBaseline(Source[i].Module.Name);
                }
            }

            return PipelineContext.New(
                option: Option,
                hostContext: HostContext,
                reader: PrepareReader(),
                bindTargetName: bindTargetName,
                bindTargetType: bindTargetType,
                bindField: bindField,
                baseline: GetOptionContext(),
                unresolved: unresolved
            );
        }

        protected virtual PipelineReader PrepareReader()
        {
            return new PipelineReader(null, null, GetInputObjectSourceFilter());
        }

        protected virtual PipelineWriter PrepareWriter()
        {
            var output = GetOutput();
            return Option.Output.Format switch
            {
                OutputFormat.Csv => new CsvOutputWriter(output, Option),
                OutputFormat.Json => new JsonOutputWriter(output, Option),
                OutputFormat.NUnit3 => new NUnit3OutputWriter(output, Option),
                OutputFormat.Yaml => new YamlOutputWriter(output, Option),
                OutputFormat.Markdown => new MarkdownOutputWriter(output, Option),
                OutputFormat.Wide => new WideOutputWriter(output, Option),
                OutputFormat.Sarif => new SarifOutputWriter(Source, output, Option),
                _ => output,
            };
        }

        protected virtual PipelineWriter GetOutput(bool writeHost = false)
        {
            // Redirect to file instead
            return !string.IsNullOrEmpty(Option.Output.Path)
                ? new FileOutputWriter(
                    inner: _Output,
                    option: Option,
                    encoding: Option.Output.GetEncoding(),
                    path: Option.Output.Path,
                    shouldProcess: HostContext.ShouldProcess,
                    writeHost: writeHost
                )
                : (PipelineWriter)_Output;
        }

        protected static string[] GetCulture(string[] culture)
        {
            var result = new List<string>();
            var parent = new List<string>();
            var set = new HashSet<string>();
            for (var i = 0; culture != null && i < culture.Length; i++)
            {
                var c = CultureInfo.CreateSpecificCulture(culture[i]);
                if (!set.Contains(c.Name))
                {
                    result.Add(c.Name);
                    set.Add(c.Name);
                }
                for (var p = c.Parent; !string.IsNullOrEmpty(p.Name); p = p.Parent)
                {
                    if (!set.Contains(p.Name))
                    {
                        parent.Add(p.Name);
                        set.Add(p.Name);
                    }
                }
            }
            if (parent.Count > 0)
                result.AddRange(parent);

            return result.Count == 0 ? null : result.ToArray();
        }

        protected static RepositoryOption GetRepository(RepositoryOption repository)
        {
            var result = new RepositoryOption(repository);
            if (string.IsNullOrEmpty(result.Url) && GitHelper.TryRepository(out var url))
                result.Url = url;

            return result;
        }

        protected PathFilter GetInputObjectSourceFilter()
        {
            return Option.Input.IgnoreObjectSource.GetValueOrDefault(InputOption.Default.IgnoreObjectSource.Value) ? GetInputFilter() : null;
        }

        protected PathFilter GetInputFilter()
        {
            if (_InputFilter == null)
            {
                var basePath = PSRuleOption.GetWorkingPath();
                var ignoreGitPath = Option.Input.IgnoreGitPath ?? InputOption.Default.IgnoreGitPath.Value;
                var ignoreRepositoryCommon = Option.Input.IgnoreRepositoryCommon ?? InputOption.Default.IgnoreRepositoryCommon.Value;
                var builder = PathFilterBuilder.Create(basePath, Option.Input.PathIgnore, ignoreGitPath, ignoreRepositoryCommon);
                if (Option.Input.Format == InputFormat.File)
                    builder.UseGitIgnore();

                _InputFilter = builder.Build();
            }
            return _InputFilter;
        }

        private OptionContext GetOptionContext()
        {
            var builder = new OptionContextBuilder(Option, _Include, _Tag, _Convention);
            return builder.Build();
        }

        protected void ConfigureBinding(PSRuleOption option)
        {
            if (option.Pipeline.BindTargetName != null && option.Pipeline.BindTargetName.Count > 0)
            {
                // Do not allow custom binding functions to be used with constrained language mode
                if (Option.Execution.LanguageMode == LanguageMode.ConstrainedLanguage)
                    throw new PipelineConfigurationException(optionName: "BindTargetName", message: PSRuleResources.ConstrainedTargetBinding);

                foreach (var action in option.Pipeline.BindTargetName)
                    BindTargetNameHook = AddBindTargetAction(action, BindTargetNameHook);
            }

            if (option.Pipeline.BindTargetType != null && option.Pipeline.BindTargetType.Count > 0)
            {
                // Do not allow custom binding functions to be used with constrained language mode
                if (Option.Execution.LanguageMode == LanguageMode.ConstrainedLanguage)
                    throw new PipelineConfigurationException(optionName: "BindTargetType", message: PSRuleResources.ConstrainedTargetBinding);

                foreach (var action in option.Pipeline.BindTargetType)
                    BindTargetTypeHook = AddBindTargetAction(action, BindTargetTypeHook);
            }
        }

        private static BindTargetMethod AddBindTargetAction(BindTargetFunc action, BindTargetMethod previous)
        {
            // Nest the previous write action in the new supplied action
            // Execution chain will be: action -> previous -> previous..n
            return (string[] propertyNames, bool caseSensitive, bool preferTargetInfo, PSObject targetObject, out string path) =>
            {
                return action(propertyNames, caseSensitive, preferTargetInfo, targetObject, previous, out path);
            };
        }

        private static BindTargetMethod AddBindTargetAction(BindTargetName action, BindTargetMethod previous)
        {
            return AddBindTargetAction((string[] propertyNames, bool caseSensitive, bool preferTargetInfo, PSObject targetObject, BindTargetMethod next, out string path) =>
            {
                path = null;
                var targetType = action(targetObject);
                return string.IsNullOrEmpty(targetType) ? next(propertyNames, caseSensitive, preferTargetInfo, targetObject, out path) : targetType;
            }, previous);
        }

        protected void AddVisitTargetObjectAction(VisitTargetObjectAction action)
        {
            // Nest the previous write action in the new supplied action
            // Execution chain will be: action -> previous -> previous..n
            var previous = VisitTargetObject;
            VisitTargetObject = (targetObject) => action(targetObject, previous);
        }

        /// <summary>
        /// Normalizes JSON indent range between minimum 0 and maximum 4.
        /// </summary>
        /// <param name="jsonIndent"></param>
        /// <returns></returns>
        protected static int NormalizeJsonIndentRange(int? jsonIndent)
        {
            if (jsonIndent.HasValue)
            {
                if (jsonIndent < MIN_JSON_INDENT)
                {
                    return MIN_JSON_INDENT;
                }

                else if (jsonIndent > MAX_JSON_INDENT)
                {
                    return MAX_JSON_INDENT;
                }

                return jsonIndent.Value;
            }

            return MIN_JSON_INDENT;
        }
    }
}
