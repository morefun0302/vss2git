﻿/* Copyright 2012 Remigius stalder, Descom Consulting Ltd.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;

namespace Hpdi.Vss2Git
{
    abstract class AbstractVcsWrapper : IVcsWrapper
    {
        private readonly string repoPath;
        private readonly Logger logger;
        private string vcs;
        private string executable;
        private string initialArguments;
        private bool shellQuoting;
        private bool needsCommit;
        private string commandFile;
        private TextWriter commandWriter;
        private readonly Stopwatch stopwatch = new Stopwatch();

        private const char QuoteChar = '"';
        private const char EscapeChar = '\\';

        protected AbstractVcsWrapper(string repoPath, Logger logger, string vcs)

        {

            this.repoPath = repoPath;
            this.logger = logger;
            this.vcs = vcs;
            needsCommit = false;
        }

        protected string AddInitialArguments(string args)
        {
            if (string.IsNullOrEmpty(initialArguments))
            {
                initialArguments = args;
            }
            else
            {
                initialArguments += " " + args;
            }
            return initialArguments;
        }

        public string RepoPath
        {
            get { return repoPath; }
        }

        public Logger Logger
        {
            get { return logger; }
        }

        public Stopwatch Stopwatch
        {
            get { return stopwatch; }
        }

        public string GetVcs()
        {
            return vcs;
        }

        public TimeSpan ElapsedTime()
        {
            return stopwatch.Elapsed;
        }


        public bool FindExecutable()
        {
            string foundPath;
            if (FindInPathVar(vcs + ".exe", out foundPath))
            {
                executable = foundPath;
                initialArguments = null;
                shellQuoting = false;
                return true;
            }
            else if (FindInPathVar(vcs + ".cmd", out foundPath))
            {
                executable = "cmd.exe";
                initialArguments = "/c " + vcs;
                shellQuoting = true;
                return true;
            }
            return false;
        }

        public class TempFile : IDisposable
        {
            private readonly string name;
            private readonly FileStream fileStream;

            public string Name
            {
                get { return name; }
            }

            public TempFile()
            {
                name = Path.GetTempFileName();
                fileStream = new FileStream(name, FileMode.Truncate, FileAccess.Write, FileShare.Read);
            }

            public void Write(string text, Encoding encoding)
            {
                var bytes = encoding.GetBytes(text);
                fileStream.Write(bytes, 0, bytes.Length);
                fileStream.Flush();
            }

            public void Dispose()
            {
                if (fileStream != null)
                {
                    fileStream.Dispose();
                }
                if (name != null)
                {
                    File.Delete(name);
                }
            }
        }

        public void VcsExec(string args)
        {
            var startInfo = GetStartInfo(args);
            ExecuteUnless(startInfo, null);
        }

        public ProcessStartInfo GetStartInfo(string args)
        {
            if (!string.IsNullOrEmpty(initialArguments))
            {
                args = initialArguments + " " + args;
            }

            var startInfo = new ProcessStartInfo(executable, args);
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.WorkingDirectory = repoPath;
            startInfo.CreateNoWindow = true;
            return startInfo;
        }

        public bool ExecuteUnless(ProcessStartInfo startInfo, string unless)
        {
            string stdout, stderr;
            int exitCode = Execute(startInfo, out stdout, out stderr);
            if (exitCode != 0)
            {
                if (string.IsNullOrEmpty(unless) ||
                    ((string.IsNullOrEmpty(stdout) || !stdout.Contains(unless)) &&
                     (string.IsNullOrEmpty(stderr) || !stderr.Contains(unless))))
                {
                    FailExitCode(startInfo.FileName, startInfo.Arguments, stdout, stderr, exitCode);
                }
            }
            return exitCode == 0;
        }

        public int Execute(ProcessStartInfo startInfo, out string stdout, out string stderr)
        {
            if (commandWriter == null && !string.IsNullOrEmpty(commandFile))
            {
                commandWriter = new StreamWriter(commandFile);
            }
            if (commandWriter != null)
            {
                commandWriter.WriteLine("{0} {1}", startInfo.FileName, startInfo.Arguments);
            }
            Logger.WriteLine("Executing: {0} {1}", startInfo.FileName, startInfo.Arguments);
            Stopwatch.Start();
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    process.StandardInput.Close();
                    var stdoutReader = new AsyncLineReader(process.StandardOutput.BaseStream);
                    var stderrReader = new AsyncLineReader(process.StandardError.BaseStream);

                    var activityEvent = new ManualResetEvent(false);
                    EventHandler activityHandler = delegate { activityEvent.Set(); };
                    process.Exited += activityHandler;
                    stdoutReader.DataReceived += activityHandler;
                    stderrReader.DataReceived += activityHandler;

                    var stdoutBuffer = new StringBuilder();
                    var stderrBuffer = new StringBuilder();
                    while (true)
                    {
                        activityEvent.Reset();

                        while (true)
                        {
                            string line = stdoutReader.ReadLine();
                            if (line != null)
                            {
                                line = line.TrimEnd();
                                if (stdoutBuffer.Length > 0)
                                {
                                    stdoutBuffer.AppendLine();
                                }
                                stdoutBuffer.Append(line);
                                Logger.Write('>');
                            }
                            else
                            {
                                line = stderrReader.ReadLine();
                                if (line != null)
                                {
                                    line = line.TrimEnd();
                                    if (stderrBuffer.Length > 0)
                                    {
                                        stderrBuffer.AppendLine();
                                    }
                                    stderrBuffer.Append(line);
                                    Logger.Write('!');
                                }
                                else
                                {
                                    break;
                                }
                            }
                            Logger.WriteLine(line);
                        }

                        if (process.HasExited)
                        {
                            break;
                        }

                        activityEvent.WaitOne(1000);
                    }

                    stdout = stdoutBuffer.ToString();
                    stderr = stderrBuffer.ToString();
                    return process.ExitCode;
                }
            }
            catch (FileNotFoundException e)
            {
                throw new ProcessException("Executable not found.",
                    e, startInfo.FileName, startInfo.Arguments);
            }
            catch (Win32Exception e)
            {
                throw new ProcessException("Error executing external process.",
                    e, startInfo.FileName, startInfo.Arguments);
            }
            finally
            {
                Stopwatch.Stop();
            }
        }

        public string QuoteRelativePath(string path)
        {
            if (path.StartsWith(repoPath))
            {
                path = path.Substring(repoPath.Length);
                if (path.StartsWith("\\") || path.StartsWith("/"))
                {
                    path = path.Substring(1);
                }
            }
            return Quote(path);
        }
        /// <summary>
        /// Puts quotes around a command-line argument if it includes whitespace
        /// or quotes.
        /// </summary>
        /// <remarks>
        /// There are two things going on in this method: quoting and escaping.
        /// Quoting puts the entire string in quotes, whereas escaping is per-
        /// character. Quoting happens only if necessary, when whitespace or a
        /// quote is encountered somewhere in the string, and escaping happens
        /// only within quoting. Spaces don't need escaping, since that's what
        /// the quotes are for. Slashes don't need escaping because apparently a
        /// backslash is only interpreted as an escape when it precedes a quote.
        /// Otherwise both slash and backslash are just interpreted as directory
        /// separators.
        /// </remarks>
        /// <param name="arg">A command-line argument to quote.</param>
        /// <returns>The given argument, possibly in quotes, with internal
        /// quotes escaped with backslashes.</returns>
        public string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return "\"\"";
            }

            StringBuilder buf = null;
            for (int i = 0; i < arg.Length; ++i)
            {
                char c = arg[i];
                if (buf == null && NeedsQuoting(c))
                {
                    buf = new StringBuilder(arg.Length + 2);
                    buf.Append(QuoteChar);
                    buf.Append(arg, 0, i);
                }
                if (buf != null)
                {
                    if (c == QuoteChar)
                    {
                        buf.Append(EscapeChar);
                    }
                    buf.Append(c);
                }
            }
            if (buf != null)
            {
                buf.Append(QuoteChar);
                return buf.ToString();
            }
            return arg;
        }


        private bool FindInPathVar(string filename, out string foundPath)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                return FindInPaths(filename, path.Split(Path.PathSeparator), out foundPath);
            }
            foundPath = null;
            return false;
        }

        private bool FindInPaths(string filename, IEnumerable<string> searchPaths, out string foundPath)
        {
            foreach (string searchPath in searchPaths)
            {
                string path = Path.Combine(searchPath, filename);
                if (File.Exists(path))
                {
                    foundPath = path;
                    return true;
                }
            }
            foundPath = null;
            return false;
        }

        private bool NeedsQuoting(char c)
        {
            return char.IsWhiteSpace(c) || c == QuoteChar ||
                (shellQuoting && (c == '&' || c == '|' || c == '<' || c == '>' || c == '^' || c == '%'));
        }

        private void FailExitCode(string exec, string args, string stdout, string stderr, int exitCode)
        {
            throw new ProcessExitException(
                string.Format(vcs + " returned exit code {0}", exitCode),
                exec, args, stdout, stderr);
        }

        public bool NeedsCommit()
        {
            return needsCommit;
        }

        public void SetNeedsCommit()
        {
            needsCommit = true;
        }

        public void Exit()
        {
            if (commandWriter != null)
            {
                commandWriter.Close();
            }
        }

        public bool Commit(string authorName, string authorEmail, string comment, DateTime localTime)
        {
            if (!needsCommit) return false;
            needsCommit = false;
            return DoCommit(authorEmail, authorEmail, comment, localTime);
        }

        public abstract void Init();
        public abstract void Configure();
        public abstract bool Add(string path);
        public abstract bool AddDir(string path);
        public abstract bool AddAll();
        public abstract void RemoveFile(string path);
        public abstract void RemoveDir(string path, bool recursive);
        public abstract void RemoveEmptyDir(string path);
        public abstract void Move(string sourcePath, string destPath);
        public abstract void MoveEmptyDir(string sourcePath, string destPath);
        public abstract bool DoCommit(string authorName, string authorEmail, string comment, DateTime localTime);
        public abstract void Tag(string name, string taggerName, string taggerEmail, string comment, DateTime localTime);
    }
}
