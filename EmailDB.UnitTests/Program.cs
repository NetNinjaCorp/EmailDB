using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace EmailDB.UnitTests;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("EmailDB Unit Tests Runner");
        Console.WriteLine("=========================\n");

        RunAllTests();

        if (args.Contains("--wait") || args.Contains("-w"))
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    private static void RunAllTests()
    {
        var testClasses = GetTestClasses();
        int totalTests = 0;
        int passedTests = 0;
        
        foreach (var testClass in testClasses)
        {
            // Skip the RunTests class to avoid recursion
            if (testClass.Name == "RunTests")
                continue;
            
            Console.WriteLine($"\nRunning tests in {testClass.Name}");
            
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
                    
                    Console.WriteLine($"  ✓ {method.Name}");
                    passedTests++;
                }
                catch (Exception ex)
                {
                    // Unwrap the inner exception if it's a TargetInvocationException
                    var actualException = ex is TargetInvocationException ? ex.InnerException : ex;
                    Console.WriteLine($"  ✗ {method.Name} - {actualException.Message}");
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
        
        Console.WriteLine($"\nTest Results: {passedTests}/{totalTests} tests passed ({(passedTests * 100.0 / totalTests):F1}% success rate)");
    }

    private static List<Type> GetTestClasses()
    {
        return Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0))
            .ToList();
    }

    private static List<MethodInfo> GetTestMethods(Type testClass)
    {
        return testClass.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0)
            .ToList();
    }
}