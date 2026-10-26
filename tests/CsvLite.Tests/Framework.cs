using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CsvLite.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute
    {
    }

    internal sealed class AssertFailedException : Exception
    {
        internal AssertFailedException(string message) : base(message) { }
    }

    internal static class Assert
    {
        internal static void True(bool condition, string? message = null)
        {
            if (!condition) throw new AssertFailedException(message ?? "Expected true but got false.");
        }

        internal static void False(bool condition, string? message = null)
            => True(!condition, message ?? "Expected false but got true.");

        internal static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertFailedException($"Expected:\n  {Show(expected)}\nActual:\n  {Show(actual)}");
        }

        internal static void Equal(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new AssertFailedException($"Expected:\n  {Show(expected)}\nActual:\n  {Show(actual)}");
        }

        internal static void NotEqual<T>(T notExpected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
                throw new AssertFailedException($"Did not expect: {Show(notExpected)}");
        }

        internal static void NotNull(object? value)
        {
            if (value == null) throw new AssertFailedException("Expected a non-null value.");
        }

        internal static void Null(object? value)
        {
            if (value != null) throw new AssertFailedException($"Expected null but got {Show(value)}.");
        }

        internal static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            T[] e = expected.ToArray();
            T[] a = actual.ToArray();
            if (e.Length != a.Length)
                throw new AssertFailedException(
                    $"Length mismatch: expected {e.Length}, actual {a.Length}.\nExpected: [{string.Join(", ", e.Select(x => Show(x)))}]\nActual:   [{string.Join(", ", a.Select(x => Show(x)))}]");
            for (int i = 0; i < e.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(e[i], a[i]))
                    throw new AssertFailedException(
                        $"Mismatch at index {i}: expected {Show(e[i])}, actual {Show(a[i])}.\nExpected: [{string.Join(", ", e.Select(x => Show(x)))}]\nActual:   [{string.Join(", ", a.Select(x => Show(x)))}]");
            }
        }

        internal static TException Throws<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new AssertFailedException(
                    $"Expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}");
            }
            throw new AssertFailedException($"Expected {typeof(TException).Name} but nothing was thrown.");
        }

        private static string Show(object? value)
        {
            if (value == null) return "<null>";
            return value switch
            {
                string s => "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"",
                _ => value.ToString() ?? "<null>",
            };
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            string? filter = args.Length > 0 ? args[0] : null;
            var methods = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || (m.DeclaringType!.Name + "." + m.Name).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.DeclaringType!.Name)
                .ThenBy(m => m.Name)
                .ToList();

            if (methods.Count == 0)
            {
                Console.WriteLine("No tests found" + (filter != null ? $" matching '{filter}'." : "."));
                return 1;
            }

            int passed = 0;
            var failures = new List<(string Name, Exception Error)>();

            foreach (MethodInfo method in methods)
            {
                string name = $"{method.DeclaringType!.Name}.{method.Name}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    method.Invoke(null, null);
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"  PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
                }
                catch (TargetInvocationException tie)
                {
                    sw.Stop();
                    Exception inner = tie.InnerException ?? tie;
                    failures.Add((name, inner));
                    Console.WriteLine($"  FAIL  {name}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Passed: {passed}  Failed: {failures.Count}  Total: {methods.Count}");

            foreach (var (name, error) in failures)
            {
                Console.WriteLine();
                Console.WriteLine($"=== {name} ===");
                Console.WriteLine($"{error.GetType().Name}: {error.Message}");
                if (error is not AssertFailedException)
                    Console.WriteLine(error.StackTrace);
            }

            return failures.Count == 0 ? 0 : 1;
        }
    }
}
