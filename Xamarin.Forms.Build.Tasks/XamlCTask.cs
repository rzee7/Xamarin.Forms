using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Xamarin.Forms.Xaml;

namespace Xamarin.Forms.Build.Tasks
{
	public class XamlCTask : AppDomainIsolatedTask
	{
		string buffer = "";

		bool hasCompiledXamlResources;

		[Required]
		public string Assembly { get; set; }

		public string DependencyPaths { get; set; }

		public string ReferencePath { get; set; }

		public int Verbosity { get; set; }

		public bool KeepXamlResources { get; set; }

		public bool OptimizeIL { get; set; }

		public bool DebugSymbols { get; set; }

		public bool OutputGeneratedILAsCode { get; set; }

		protected bool InMsBuild { get; set; }

		public override bool Execute()
		{
			InMsBuild = true;
			return Compile();
		}

		protected void LogError(string subcategory, string errorCode, string helpKeyword, string file, int lineNumber,
			int columnNumber, int endLineNumber, int endColumnNumber, string message, params object[] messageArgs)
		{
			if (!string.IsNullOrEmpty(buffer))
				LogLine(-1, null, null);
			if (InMsBuild)
			{
				base.Log.LogError(subcategory, errorCode, helpKeyword, file, lineNumber, columnNumber, endLineNumber,
					endColumnNumber, message, messageArgs);
			}
			else
				Console.Error.WriteLine("{0} ({1}:{2}) : {3}", file, lineNumber, columnNumber, message);
		}

		protected void LogLine(int level, string format, params object[] arg)
		{
			if (!string.IsNullOrEmpty(buffer))
			{
				format = buffer + format;
				buffer = "";
			}

			if (level < 0)
			{
				if (InMsBuild)
					base.Log.LogError(format, arg);
				else
					Console.Error.WriteLine(format, arg);
			}
			else if (level <= Verbosity)
			{
				if (InMsBuild)
					base.Log.LogMessage(format, arg);
				else
					Console.WriteLine(format, arg);
			}
		}

		protected void LogString(int level, string format, params object[] arg)
		{
			if (level <= 0)
				Console.Error.Write(format, arg);
			else if (level <= Verbosity)
			{
				if (InMsBuild)
					buffer += String.Format(format, arg);
				else
					Console.Write(format, arg);
			}
		}

		public static void Compile(string assemblyFileName, int verbosity = 0, bool keep = false, bool optimize = false,
			string dependencyPaths = null, string referencePath = null, bool outputCSharp = false)
		{
			var xamlc = new XamlCTask
			{
				Assembly = assemblyFileName,
				Verbosity = verbosity,
				KeepXamlResources = keep,
				OptimizeIL = optimize,
				InMsBuild = false,
				DependencyPaths = dependencyPaths,
				ReferencePath = referencePath,
				OutputGeneratedILAsCode = outputCSharp
			};
			xamlc.Compile();
		}

		public bool Compile()
		{
			LogLine(1, "Compiling Xaml");
			LogLine(1, "\nAssembly: {0}", Assembly);
			if (!string.IsNullOrEmpty(DependencyPaths))
				LogLine(1, "DependencyPaths: \t{0}", DependencyPaths);
			if (!string.IsNullOrEmpty(ReferencePath))
				LogLine(1, "ReferencePath: \t{0}", ReferencePath.Replace("//", "/"));
			LogLine(3, "DebugSymbols:\"{0}\"", DebugSymbols);
			var skipassembly = true; //change this to false to enable XamlC by default
			bool success = true;

			if (!File.Exists(Assembly))
			{
				LogLine(1, "Assembly file not found. Skipping XamlC.");
				return true;
			}

			var resolver = new XamlCAssemblyResolver();
			if (!string.IsNullOrEmpty(DependencyPaths))
			{
				foreach (var dep in DependencyPaths.Split(';'))
				{
					LogLine(3, "Adding searchpath {0}", dep);
					resolver.AddSearchDirectory(dep);
				}
			}

			if (!string.IsNullOrEmpty(ReferencePath))
			{
				var paths = ReferencePath.Replace("//", "/").Split(';');
				foreach (var p in paths)
				{
					var searchpath = Path.GetDirectoryName(p);
					LogLine(3, "Adding searchpath {0}", searchpath);
					resolver.AddSearchDirectory(searchpath);
					//					LogLine (3, "Referencing {0}", p);
					//					resolver.AddAssembly (p);
				}
			}

			var assemblyDefinition = AssemblyDefinition.ReadAssembly(Path.GetFullPath(Assembly), new ReaderParameters
			{
				AssemblyResolver = resolver,
				ReadSymbols = DebugSymbols
			});

			CustomAttribute xamlcAttr;
			if (assemblyDefinition.HasCustomAttributes &&
			    (xamlcAttr =
				    assemblyDefinition.CustomAttributes.FirstOrDefault(
					    ca => ca.AttributeType.FullName == "Xamarin.Forms.Xaml.XamlCompilationAttribute")) != null)
			{
				var options = (XamlCompilationOptions)xamlcAttr.ConstructorArguments[0].Value;
				if ((options & XamlCompilationOptions.Skip) == XamlCompilationOptions.Skip)
					skipassembly = true;
				if ((options & XamlCompilationOptions.Compile) == XamlCompilationOptions.Compile)
					skipassembly = false;
			}

			foreach (var module in assemblyDefinition.Modules)
			{
				var skipmodule = skipassembly;
				if (module.HasCustomAttributes &&
				    (xamlcAttr =
					    module.CustomAttributes.FirstOrDefault(
						    ca => ca.AttributeType.FullName == "Xamarin.Forms.Xaml.XamlCompilationAttribute")) != null)
				{
					var options = (XamlCompilationOptions)xamlcAttr.ConstructorArguments[0].Value;
					if ((options & XamlCompilationOptions.Skip) == XamlCompilationOptions.Skip)
						skipmodule = true;
					if ((options & XamlCompilationOptions.Compile) == XamlCompilationOptions.Compile)
						skipmodule = false;
				}

				LogLine(2, " Module: {0}", module.Name);
				var resourcesToPrune = new List<EmbeddedResource>();
				foreach (var resource in module.Resources.OfType<EmbeddedResource>())
				{
					LogString(2, "  Resource: {0}... ", resource.Name);
					string classname;
					if (!resource.IsXaml(out classname))
					{
						LogLine(2, "skipped.");
						continue;
					}
					TypeDefinition typeDef = module.GetType(classname);
					if (typeDef == null)
					{
						LogLine(2, "no type found... skipped.");
						continue;
					}
					var skiptype = skipmodule;
					if (typeDef.HasCustomAttributes &&
					    (xamlcAttr =
						    typeDef.CustomAttributes.FirstOrDefault(
							    ca => ca.AttributeType.FullName == "Xamarin.Forms.Xaml.XamlCompilationAttribute")) != null)
					{
						var options = (XamlCompilationOptions)xamlcAttr.ConstructorArguments[0].Value;
						if ((options & XamlCompilationOptions.Skip) == XamlCompilationOptions.Skip)
							skiptype = true;
						if ((options & XamlCompilationOptions.Compile) == XamlCompilationOptions.Compile)
							skiptype = false;
					}
					if (skiptype)
					{
						LogLine(2, "Has XamlCompilationAttribute set to Skip and not Compile... skipped");
						continue;
					}

					var initComp = typeDef.Methods.FirstOrDefault(md => md.Name == "InitializeComponent");
					if (initComp == null)
					{
						LogLine(2, "no InitializeComponent found... skipped.");
						continue;
					}
					LogLine(2, "");

					LogString(2, "   Parsing Xaml... ");
					var rootnode = ParseXaml(resource.GetResourceStream(), typeDef);
					if (rootnode == null)
					{
						LogLine(2, "failed.");
						continue;
					}
					LogLine(2, "done.");

					hasCompiledXamlResources = true;

					try
					{
						LogString(2, "   Replacing {0}.InitializeComponent ()... ", typeDef.Name);
						var body = new MethodBody(initComp);
						var il = body.GetILProcessor();
						il.Emit(OpCodes.Nop);
						var visitorContext = new ILContext(il, body);

						rootnode.Accept(new XamlNodeVisitor((node, parent) => node.Parent = parent), null);
						rootnode.Accept(new ExpandMarkupsVisitor(visitorContext), null);
						rootnode.Accept(new CreateObjectVisitor(visitorContext), null);
						rootnode.Accept(new SetNamescopesAndRegisterNamesVisitor(visitorContext), null);
						rootnode.Accept(new SetFieldVisitor(visitorContext), null);
						rootnode.Accept(new SetResourcesVisitor(visitorContext), null);
						rootnode.Accept(new SetPropertiesVisitor(visitorContext, true), null);

						il.Emit(OpCodes.Ret);
						initComp.Body = body;
					}
					catch (XamlParseException xpe)
					{
						LogLine(2, "failed.");
						LogError(null, null, null, resource.Name, xpe.XmlInfo.LineNumber, xpe.XmlInfo.LinePosition, 0, 0, xpe.Message,
							xpe.HelpLink, xpe.Source);
						LogLine(4, xpe.StackTrace);
						success = false;
						continue;
					}
					catch (XmlException xe)
					{
						LogLine(2, "failed.");
						LogError(null, null, null, resource.Name, xe.LineNumber, xe.LinePosition, 0, 0, xe.Message, xe.HelpLink, xe.Source);
						LogLine(4, xe.StackTrace);
						success = false;
						continue;
					}
					catch (Exception e)
					{
						LogLine(2, "failed.");
						LogError(null, null, null, resource.Name, 0, 0, 0, 0, e.Message, e.HelpLink, e.Source);
						LogLine(4, e.StackTrace);
						success = false;
						continue;
					}
					LogLine(2, "done.");

					if (OptimizeIL)
					{
						LogString(2, "   Optimizing IL... ");
						initComp.Body.OptimizeMacros();
						LogLine(2, "done");
					}

					if (OutputGeneratedILAsCode)
					{
						var filepath = Path.Combine(Path.GetDirectoryName(Assembly), typeDef.FullName + ".decompiled.cs");
						LogString(2, "   Decompiling {0} into {1}...", typeDef.FullName, filepath);
						var decompilerContext = new DecompilerContext(module);
						using (var writer = new StreamWriter(filepath))
						{
							var output = new PlainTextOutput(writer);

							var codeDomBuilder = new AstBuilder(decompilerContext);
							codeDomBuilder.AddType(typeDef);
							codeDomBuilder.GenerateCode(output);
						}

						LogLine(2, "done");
					}
					resourcesToPrune.Add(resource);
				}
				if (!KeepXamlResources)
				{
					if (resourcesToPrune.Any())
						LogLine(2, "  Removing compiled xaml resources");
					foreach (var resource in resourcesToPrune)
					{
						LogString(2, "   Removing {0}... ", resource.Name);
						module.Resources.Remove(resource);
						LogLine(2, "done");
					}
				}

				LogLine(2, "");
			}

			if (!hasCompiledXamlResources)
			{
				LogLine(1, "No compiled resources. Skipping writing assembly.");
				return success;
			}

			LogString(1, "Writing the assembly... ");
			try
			{
				assemblyDefinition.Write(Assembly, new WriterParameters
				{
					WriteSymbols = DebugSymbols
				});
				LogLine(1, "done.");
			}
			catch (Exception e)
			{
				LogLine(1, "failed.");
				LogError(null, null, null, null, 0, 0, 0, 0, e.Message, e.HelpLink, e.Source);
				LogLine(4, e.StackTrace);
				success = false;
			}

			return success;
		}

		static ILRootNode ParseXaml(Stream stream, TypeReference typeReference)
		{
			ILRootNode rootnode = null;
			using (var reader = XmlReader.Create(stream))
			{
				while (reader.Read())
				{
					//Skip until element
					if (reader.NodeType == XmlNodeType.Whitespace)
						continue;
					if (reader.NodeType != XmlNodeType.Element)
					{
						Debug.WriteLine("Unhandled node {0} {1} {2}", reader.NodeType, reader.Name, reader.Value);
						continue;
					}

					XamlParser.ParseXaml(
						rootnode = new ILRootNode(new XmlType(reader.NamespaceURI, reader.Name, null), typeReference), reader);
					break;
				}
			}
			return rootnode;
		}
	}

	static class CecilExtensions
	{
		public static bool IsXaml(this EmbeddedResource resource, out string classname)
		{
			classname = null;
			if (!resource.Name.EndsWith(".xaml", StringComparison.InvariantCulture))
				return false;

			using (var resourceStream = resource.GetResourceStream())
			{
				var xmlDoc = new XmlDocument();
				xmlDoc.Load(resourceStream);

				var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);

				var root = xmlDoc.SelectSingleNode("/*", nsmgr);
				if (root == null)
				{
					//					Log (2, "No root found... ");
					return false;
				}

				var rootClass = root.Attributes["Class", "http://schemas.microsoft.com/winfx/2006/xaml"] ??
				                root.Attributes["Class", "http://schemas.microsoft.com/winfx/2009/xaml"];
				if (rootClass == null)
				{
					//					Log (2, "no x:Class found... ");
					return false;
				}
				classname = rootClass.Value;
				return true;
			}
		}
	}
}