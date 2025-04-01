using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests
{
    public class RunTests
    {
        private readonly ITestOutputHelper output;

        // Default constructor for test discovery
        public RunTests()
        {
            // Use Console.WriteLine for output when no ITestOutputHelper is provided
        }

        public RunTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private void WriteLine(string message)
        {
            if (output != null)
            {
                output.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        [Fact]
        public void RunAllTests()
        {
            WriteLine("Running all unit tests...");
            
            var testClasses = GetTestClasses();
            int totalTests = 0;
            int passedTests = 0;
            
            foreach (var testClass in testClasses)
            {
                WriteLine($"\nRunning tests in {testClass.Name}");
                
                var testMethods = GetTestMethods(testClass);
                totalTests += testMethods.Count;
                
                foreach (var method in testMethods)
                {
                    object instance = null;
                    try
                    {
                        // Create an instance of the test class
                        instance = Activator.CreateInstance(testClass);
                        
                        // Run the test method
                        method.Invoke(instance, null);
                        
                        WriteLine($"  ✓ {method.Name}");
                        passedTests++;
                    }
                    catch (Exception ex)
                    {
                        // Unwrap the inner exception if it's a TargetInvocationException
                        var actualException = ex is TargetInvocationException ? ex.InnerException : ex;
                        WriteLine($"  ✗ {method.Name} - {actualException.Message}");
                    }
                    finally
                    {
                        // If the test class implements IDisposable, call Dispose
                        if (instance is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                }
            }
            
            WriteLine($"\nTest Results: {passedTests}/{totalTests} tests passed ({(passedTests * 100.0 / totalTests):F1}% success rate)");
        }

        private List<Type> GetTestClasses()
        {
            return Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0))
                .ToList();
        }

        private List<MethodInfo> GetTestMethods(Type testClass)
        {
            return testClass.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0)
                .ToList();
        }
    }
}