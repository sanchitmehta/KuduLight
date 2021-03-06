﻿using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Oryx;
using System;
using System.Collections.Generic;
using Xunit;

namespace Kudu.Tests.Core.Deployment.Oryx
{
    [Collection("MockedEnvironmentVariablesCollection")]
    public class OryxArgumentsFactoryTests
    {
        [Fact]
        public void OryxArgumentShouldBeAppService()
        {
            IEnvironment ienv = new TestMockedIEnvironment();
            IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
            Assert.IsType<AppServiceOryxArguments>(args);
        }

        [Fact]
        public void OryxArgumentShouldBeFunctionApp()
        {
            using (new TestScopedEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME", "PYTHON"))
            using (new TestScopedEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION", "~2"))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                Assert.IsType<FunctionAppOryxArguments>(args);
            }
        }

        [Fact]
        public void OryxArgumentShouldBeLinuxConsumption()
        {
            using (new TestScopedEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME", "PYTHON"))
            using (new TestScopedEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION", "~2"))
            using (new TestScopedEnvironmentVariable("SCM_RUN_FROM_PACKAGE", "http://microsoft.com"))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                Assert.IsType<LinuxConsumptionFunctionAppOryxArguments>(args);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(false, "FUNCTIONS_EXTENSION_VERSION", "~2")]
        [InlineData(false, "FUNCTIONS_EXTENSION_VERSION", "~2", "SCM_RUN_FROM_PACKAGE", "http://microsoft.com")]
        [InlineData(true, "FUNCTIONS_EXTENSION_VERSION", "~2", "FUNCTIONS_WORKER_RUNTIME", "PYTHON")]
        [InlineData(true, "FUNCTIONS_EXTENSION_VERSION", "~2", "SCM_RUN_FROM_PACKAGE", "http://microsoft.com", "FUNCTIONS_WORKER_RUNTIME", "PYTHON")]
        public void OryxArgumentRunOryxBuild(bool expectedRunOryxBuild, params string[] varargs)
        {
            IDictionary<string, string> env = new Dictionary<string, string>();
            for (int i = 0; i < varargs.Length; i += 2)
            {
                env.Add(varargs[i], varargs[i + 1]);
            }

            using (new TestScopedEnvironmentVariable(env))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                Assert.Equal(expectedRunOryxBuild, args.RunOryxBuild);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(false, "FUNCTIONS_EXTENSION_VERSION", "~2")]
        [InlineData(true, "FUNCTIONS_EXTENSION_VERSION", "~2", "SCM_RUN_FROM_PACKAGE", "http://microsoft.com")]
        [InlineData(false, "FUNCTIONS_EXTENSION_VERSION", "~2", "FUNCTIONS_WORKER_RUNTIME", "PYTHON")]
        [InlineData(true, "FUNCTIONS_EXTENSION_VERSION", "~2", "SCM_RUN_FROM_PACKAGE", "http://microsoft.com", "FUNCTIONS_WORKER_RUNTIME", "PYTHON")]
        public void OryxArgumentSkipKuduSync(bool expectedSkipKuduSync, params string[] varargs)
        {
            IDictionary<string, string> env = new Dictionary<string, string>();
            for (int i = 0; i < varargs.Length; i += 2)
            {
                env.Add(varargs[i], varargs[i + 1]);
            }

            using (new TestScopedEnvironmentVariable(env))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                Assert.Equal(expectedSkipKuduSync, args.SkipKuduSync);
            }
        }

        [Fact]
        public void BuildCommandForAppService()
        {
            DeploymentContext deploymentContext = new DeploymentContext()
            {
                OutputPath = "outputpath"
            };
            IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
            IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
            string command = args.GenerateOryxBuildCommand(deploymentContext);
            Assert.Equal(@"oryx build outputpath -o outputpath", command);
        }

        [Fact]
        public void BuildCommandForFunctionApp()
        {
            DeploymentContext deploymentContext = new DeploymentContext()
            {
                OutputPath = "outputpath",
                BuildTempPath = "buildtemppath"
            };

            using (new TestScopedEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION", "~2"))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                string command = args.GenerateOryxBuildCommand(deploymentContext);
                Assert.Equal(@"oryx build outputpath -o outputpath -i buildtemppath", command);
            }
        }

        [Fact]
        public void BuildCommandForLinuxConsumptionFunctionApp()
        {
            DeploymentContext deploymentContext = new DeploymentContext()
            {
                RepositoryPath = "repositorypath"
            };

            using (new TestScopedEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION", "~2"))
            using (new TestScopedEnvironmentVariable("SCM_RUN_FROM_PACKAGE", "http://microsoft.com"))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                string command = args.GenerateOryxBuildCommand(deploymentContext);
                Assert.Equal(@"oryx build repositorypath -o repositorypath", command);
            }
        }

        [Fact]
        public void BuildCommandForPythonFunctionApp()
        {
            DeploymentContext deploymentContext = new DeploymentContext()
            {
                OutputPath = "outputpath",
                BuildTempPath = "buildtemppath"
            };

            using (new TestScopedEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION", "~2"))
            using (new TestScopedEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME", "python"))
            {
                IEnvironment ienv = TestMockedEnvironment.GetMockedEnvironment();
                IOryxArguments args = OryxArgumentsFactory.CreateOryxArguments(ienv);
                string command = args.GenerateOryxBuildCommand(deploymentContext);
                Assert.Equal(@"oryx build outputpath -o outputpath --platform python --platform-version 3.6 -i buildtemppath -p packagedir=.python_packages\lib\python3.6\site-packages", command);
            }
        }
    }
}
