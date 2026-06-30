/*
 * App.xaml.cs - part of Grbl Code Sender
 *
 * v0.37 / 2021-02-20 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2022, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace GCode_Sender
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {

        public App()
        {
            // Pin the resource assembly to this exe BEFORE InitializeComponent loads App.xaml (the first
            // pack-resource access). Under the VS debugger / hosting process GetEntryAssembly() can resolve to a
            // different assembly, so the default lookup fails to find ioSender's compiled BAML and startup dies
            // with "Cannot locate resource 'splashwindow.xaml'". Setting it explicitly fixes that regardless of
            // how the process was launched.
            if (System.Windows.Application.ResourceAssembly == null)
                System.Windows.Application.ResourceAssembly = System.Reflection.Assembly.GetExecutingAssembly();

            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            Application.Current.DispatcherUnhandledException += DispatcherOnUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();

            int p = 0, lng = 0;
            while (p < args.GetLength(0)) switch (args[p++])
            {
                case "-locale":
                    if (p < args.GetLength(0))
                        lng = p;
                    break;
            }

            if (lng > 0)
            {
                Thread.CurrentThread.CurrentUICulture =
                 Thread.CurrentThread.CurrentCulture =
                  CultureInfo.DefaultThreadCurrentCulture =
                   CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(args[lng]); ;

                FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
            }

            base.OnStartup(e);

            // Show a status splash, then create the main window invisible (StartupUri removed from App.xaml).
            // CompleteStartup connects/reads settings/validates the machine-setup steps with the splash up, then
            // reveals the main window (on the Machine Setup tab if setup is incomplete) and closes the splash.
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var splash = new SplashWindow();
            splash.Show();

            var main = new MainWindow();
            Current.MainWindow = main;
            main.AttachSplash(splash);
            main.Show();   // shown with Opacity 0 / not in taskbar; Window_Load -> CompleteStartup runs unseen
        }

        private void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        { 
            MessageBox.Show("Unhandled exception occured: " + (args.ExceptionObject as Exception).Message, "CurrentDomainException");
        }

        private void DispatcherOnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            args.Handled = true;

            MessageBox.Show("Unhandled exception occured: " + (args.Exception as Exception).Message, "DispatcherException");
            Environment.Exit(-1);
        }

        private void TaskSchedulerOnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            args.SetObserved();

            MessageBox.Show("Unhandled exception occured: " + (args.Exception.GetBaseException() as Exception).Message, "TaskSchedulerException");
        }
    }
}
