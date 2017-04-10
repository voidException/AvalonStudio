﻿namespace AvalonStudio.Debugging
{
    using Avalonia.Threading;
    using AvalonStudio.Documents;
    using AvalonStudio.Extensibility;
    using AvalonStudio.Extensibility.Plugin;
    using AvalonStudio.Platforms;
    using AvalonStudio.Projects;
    using AvalonStudio.Shell;
    using AvalonStudio.Utils;
    using Mono.Debugging.Client;
    using System.Xml;
    using System;

    public class DebugManager2 : IDebugManager2, IExtension
    {
        private DebuggerSession _session;
        private IShell _shell;
        private IConsole _console;
        private IEditor _lastDocument;

        public DebugManager2()
        {
            Breakpoints = new BreakpointStore();

            Breakpoints.BreakpointAdded += (sender, e) =>
            {
                SaveBreakpoints();
            };

            Breakpoints.BreakpointRemoved += (sender, e) =>
            {
                SaveBreakpoints();
            };
        }

        private bool _loadingBreakpoints;

        private void SaveBreakpoints()
        {
            if (!_loadingBreakpoints)
            {
                var solution = _shell.CurrentSolution;

                Platform.EnsureSolutionUserDataDirectory(solution);

                var file = System.IO.Path.Combine(Platform.GetUserDataDirectory(solution), "Breakpoints.xml");

                using (var writer = XmlWriter.Create(file))
                {
                    Breakpoints.Save().WriteTo(writer);
                }
            }
        }

        private void LoadBreakpoints()
        {
            _loadingBreakpoints = true;

            var solution = _shell.CurrentSolution;

            if (solution != null)
            {
                var file = System.IO.Path.Combine(Platform.GetUserDataDirectory(solution), "Breakpoints.xml");

                if (System.IO.File.Exists(file))
                {
                    using (var reader = XmlReader.Create(file))
                    {
                        var doc = new XmlDocument();
                        doc.Load(reader);

                        Breakpoints.Load(doc.DocumentElement);
                    }
                }
            }
            else
            {
                Breakpoints.Clear();
            }

            _loadingBreakpoints = false;
        }

        public BreakpointStore Breakpoints { get; set; }

        public bool SessionActive => _session != null;

        public void Activation()
        {
            _shell = IoC.Get<IShell>();
            _console = IoC.Get<IConsole>();

            _shell.SolutionChanged += (sender, e) =>
            {
                LoadBreakpoints();
            };
        }

        public void BeforeActivation()
        {
            IoC.RegisterConstant<IDebugManager2>(this);
        }

        private void OnEndSession()
        {
            _shell.CurrentPerspective = Perspective.Editor;
            _session?.Dispose();
            _session = null;
        }

        public void Restart()
        {
            OnEndSession();
            Start();
        }

        public async void Start()
        {
                var project = _shell.GetDefaultProject();

                if (project == null)
                {
                    OnEndSession();
                    _console.WriteLine("No Default project set. Please set a default project before debugging.");
                    return;
                }

                if (!await project.ToolChain.Build(_console, project))
                {
                    OnEndSession();
                    return;
                }

                if(project.Debugger2 == null)
                {
                    OnEndSession();
                    _console.WriteLine("No Debug adaptor is set for default project.");
                    return;
                }

                _session = project.Debugger2.CreateSession();

                _session.Breakpoints = Breakpoints;

                _session.Run(project.Debugger2.GetDebuggerStartInfo(project), project.Debugger2.GetDebuggerSessionOptions(project));

                _session.TargetStopped += _session_TargetStopped;

                _session.TargetEvent += (sender, e) =>
                {
                    _console.WriteLine(e.Type.ToString());
                };

                _session.TargetHitBreakpoint += _session_TargetStopped;

                _session.TargetExited += (sender, e) =>
                {
                    OnEndSession();

                    if (_lastDocument != null)
                    {
                        _lastDocument.ClearDebugHighlight();
                        _lastDocument = null;
                    }
                };

                _session.TargetStarted += (sender, e) =>
                {
                    if (_lastDocument != null)
                    {
                        _lastDocument.ClearDebugHighlight();
                        _lastDocument = null;
                    }
                };

                _session.OutputWriter = (stdError, text) =>
                {
                    _console.Write(text);
                };

                _shell.CurrentPerspective = Perspective.Debug;
        }

        private void _session_TargetStopped(object sender, TargetEventArgs e)
        {
            var sourceLocation = e.Backtrace.GetFrame(0).SourceLocation;

            var normalizedPath = sourceLocation.FileName.Replace("\\\\", "\\").NormalizePath();

            ISourceFile file = null;

            var document = _shell.GetDocument(normalizedPath);

            if (document != null)
            {
                _lastDocument = document;
                file = document?.ProjectFile;
            }

            if (file == null)
            {
                file = _shell.CurrentSolution.FindFile(normalizedPath);
            }

            if (file != null)
            {
                Dispatcher.UIThread.InvokeAsync(async () => { _lastDocument = await _shell.OpenDocument(file, sourceLocation.Line, 1, true); });
            }
            else
            {
                _console.WriteLine("Unable to find file: " + normalizedPath);
            }
        }

        public void SteoOver()
        {
            _session?.NextLine();
        }

        public void Continue()
        {
            _session?.Continue();
        }

        public void StepInto()
        {
            _session?.StepLine();
        }

        public void StepInstruction()
        {
            _session?.StepInstruction();
        }

        public void StepOut()
        {
            _session?.Finish();
        }
    }
}
