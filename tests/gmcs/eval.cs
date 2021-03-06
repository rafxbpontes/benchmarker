//
// eval.cs: Evaluation and Hosting API for the C# compiler
//
// Authors:
//   Miguel de Icaza (miguel@gnome.org)
//
// Dual licensed under the terms of the MIT X11 or GNU GPL
//
// Copyright 2001, 2002, 2003 Ximian, Inc (http://www.ximian.com)
// Copyright 2004, 2005, 2006, 2007, 2008 Novell, Inc
//
using System;
using System.Threading;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using System.Globalization;
using System.Text;

namespace Mono.CSharp {

	/// <summary>
	///   Evaluator: provides an API to evaluate C# statements and
	///   expressions dynamically.
	/// </summary>
	/// <remarks>
	///   This class exposes static methods to evaluate expressions in the
	///   current program.
	///
	///   To initialize the evaluator with a number of compiler
	///   options call the Init(string[]args) method with a set of
	///   command line options that the compiler recognizes.
	///
	///   To interrupt execution of a statement, you can invoke the
	///   Evaluator.Interrupt method.
	/// </remarks>
	public class Evaluator {

		static object evaluator_lock = new object ();
		
		static string current_debug_name;
		static int count;
		static Thread invoke_thread;
		
		static ArrayList using_alias_list = new ArrayList ();
		static ArrayList using_list = new ArrayList ();
		static Hashtable fields = new Hashtable ();

		static Type   interactive_base_class = typeof (InteractiveBase);
		static Driver driver;
		static bool inited;

		/// <summary>
		///   Optional initialization for the Evaluator.
		/// </summary>
		/// <remarks>
		///  Initializes the Evaluator with the command line options
		///  that would be processed by the command line compiler.  Only
		///  the first call to Init will work, any future invocations are
		///  ignored.
		///
		///  You can safely avoid calling this method if your application
		///  does not need any of the features exposed by the command line
		///  interface.
		/// </remarks>
		public static void Init (string [] args)
		{
			lock (evaluator_lock){
				if (inited)
					return;
				
				RootContext.Version = LanguageVersion.Default;
				driver = Driver.Create (args, false);
				if (driver == null)
					throw new Exception ("Failed to create compiler driver with the given arguments");
				
				driver.ProcessDefaultConfig ();
				CompilerCallableEntryPoint.Reset ();
				Driver.LoadReferences ();
				RootContext.EvalMode = true;
				inited = true;
			}
		}

		static void Init ()
		{
			Init (new string [0]);
		}
		
		static void Reset ()
		{
			CompilerCallableEntryPoint.PartialReset ();

			//
			// PartialReset should not reset the core types, this is very redundant.
			//
			if (!TypeManager.InitCoreTypes ())
				throw new Exception ("Failed to InitCoreTypes");
			TypeManager.InitOptionalCoreTypes ();
			
			Location.AddFile ("{interactive}");
			Location.Initialize ();

			current_debug_name = "interactive" + (count++) + ".dll";
			if (Environment.GetEnvironmentVariable ("SAVE") != null){
				CodeGen.Init (current_debug_name, current_debug_name, false);
			} else
				CodeGen.InitDynamic (current_debug_name);
		}

		/// <summary>
		///   The base class for the classes that host the user generated code
		/// </summary>
		/// <remarks>
		///
		///   This is the base class that will host the code
		///   executed by the Evaluator.  By default
		///   this is the Mono.CSharp.InteractiveBase class
		///   which is useful for interactive use.
		///
		///   By changing this property you can control the
		///   base class and the static members that are
		///   available to your evaluated code.
		/// </remarks>
		static public Type InteractiveBaseClass {
			get {
				return interactive_base_class;
			}

			set {
				if (value == null)
					throw new ArgumentNullException ();

				lock (evaluator_lock)
					interactive_base_class = value;
			}
		}

		/// <summary>
		///   Interrupts the evaluation of an expression executing in Evaluate.
		/// </summary>
		/// <remarks>
		///   Use this method to interrupt long-running invocations.
		/// </remarks>
		public static void Interrupt ()
		{
			if (!inited || !invoking)
				return;
			
			if (invoke_thread != null)
				invoke_thread.Abort ();
		}

		/// <summary>
		///   Compiles the input string and returns a delegate that represents the compiled code.
		/// </summary>
		/// <remarks>
		///
		///   Compiles the input string as a C# expression or
		///   statement, unlike the Evaluate method, the
		///   resulting delegate can be invoked multiple times
		///   without incurring in the compilation overhead.
		///
		///   If the return value of this function is null,
		///   this indicates that the parsing was complete.
		///   If the return value is a string it indicates
		///   that the input string was partial and that the
		///   invoking code should provide more code before
		///   the code can be successfully compiled.
		///
		///   If you know that you will always get full expressions or
		///   statements and do not care about partial input, you can use
		///   the other Compile overload. 
		///
		///   On success, in addition to returning null, the
		///   compiled parameter will be set to the delegate
		///   that can be invoked to execute the code.
		///
	        /// </remarks>
		static public string Compile (string input, out CompiledMethod compiled)
		{
			if (input == null || input.Length == 0){
				compiled = null;
				return null;
			}
			
			lock (evaluator_lock){
				if (!inited)
					Init ();
				
				bool partial_input;
				CSharpParser parser = ParseString (true, input, out partial_input);
				if (parser == null){
					compiled = null;
					if (partial_input)
						return input;
					
					ParseString (false, input, out partial_input);
					return null;
				}
				
				object parser_result = parser.InteractiveResult;
				
				if (!(parser_result is Class)){
					int errors = Report.Errors;
					
					NamespaceEntry.VerifyAllUsing ();
					if (errors == Report.Errors)
						parser.CurrentNamespace.Extract (using_alias_list, using_list);
				}

				compiled = CompileBlock (parser_result as Class, parser.undo);
			}
			
			return null;
		}

		/// <summary>
		///   Compiles the input string and returns a delegate that represents the compiled code.
		/// </summary>
		/// <remarks>
		///
		///   Compiles the input string as a C# expression or
		///   statement, unlike the Evaluate method, the
		///   resulting delegate can be invoked multiple times
		///   without incurring in the compilation overhead.
		///
		///   This method can only deal with fully formed input
		///   strings and does not provide a completion mechanism.
		///   If you must deal with partial input (for example for
		///   interactive use) use the other overload. 
		///
		///   On success, a delegate is returned that can be used
		///   to invoke the method.
		///
	        /// </remarks>
		static public CompiledMethod Compile (string input)
		{
			CompiledMethod compiled;

			// Ignore partial inputs
			if (Compile (input, out compiled) != null){
				// Error, the input was partial.
				return null;
			}

			// Either null (on error) or the compiled method.
			return compiled;
		}
		
		//
		// Todo: Should we handle errors, or expect the calling code to setup
		// the recording themselves?
		//

		/// <summary>
		///   Evaluates and expression or statement and returns any result values.
		/// </summary>
		/// <remarks>
		///   Evaluates the input string as a C# expression or
		///   statement.  If the input string is an expression
		///   the result will be stored in the result variable
		///   and the result_set variable will be set to true.
		///
		///   It is necessary to use the result/result_set
		///   pair to identify when a result was set (for
		///   example, execution of user-provided input can be
		///   an expression, a statement or others, and
		///   result_set would only be set if the input was an
		///   expression.
		///
		///   If the return value of this function is null,
		///   this indicates that the parsing was complete.
		///   If the return value is a string, it indicates
		///   that the input is partial and that the user
		///   should provide an updated string.
		/// </remarks>
		public static string Evaluate (string input, out object result, out bool result_set)
		{
			CompiledMethod compiled;

			result_set = false;
			result = null;

			input = Compile (input, out compiled);
			if (input != null)
				return input;
			
			if (compiled == null)
				return null;
				
			//
			// The code execution does not need to keep the compiler lock
			//
			object retval = typeof (NoValueSet);
			
			try {
				invoke_thread = System.Threading.Thread.CurrentThread;
				invoking = true;
				compiled (ref retval);
			} catch (ThreadAbortException e){
				Thread.ResetAbort ();
				Console.WriteLine ("Interrupted!\n{0}", e);
			} finally {
				invoking = false;
			}

			//
			// We use a reference to a compiler type, in this case
			// Driver as a flag to indicate that this was a statement
			//
			if (retval != typeof (NoValueSet)){
				result_set = true;
				result = retval; 
			}

			return null;
		}
			
		/// <summary>
		///   Executes the given expression or statement.
		/// </summary>
		/// <remarks>
		///    Executes the provided statement, returns true
		///    on success, false on parsing errors.  Exceptions
		///    might be thrown by the called code.
		/// </remarks>
		public static bool Run (string statement)
		{
			if (!inited)
				Init ();

			object result;
			bool result_set;

			bool ok = Evaluate (statement, out result, out result_set) == null;
			
			return ok;
		}

		/// <summary>
		///   Evaluates and expression or statement and returns the result.
		/// </summary>
		/// <remarks>
		///   Evaluates the input string as a C# expression or
		///   statement and returns the value.   
		///
		///   This method will throw an exception if there is a syntax error,
		///   of if the provided input is not an expression but a statement.
		/// </remarks>
		public static object Evaluate (string input)
		{
			object result;
			bool result_set;
			
			string r = Evaluate (input, out result, out result_set);

			if (r != null)
				throw new ArgumentException ("Syntax error on input: partial input");
			
			if (result_set == false)
				throw new ArgumentException ("The expression did not set a result");

			return result;
		}
		
		enum InputKind {
			EOF,
			StatementOrExpression,
			CompilationUnit,
			Error
		}

		//
		// Deambiguates the input string to determine if we
		// want to process a statement or if we want to
		// process a compilation unit.
		//
		// This is done using a top-down predictive parser,
		// since the yacc/jay parser can not deambiguage this
		// without more than one lookahead token.   There are very
		// few ambiguities.
		//
		static InputKind ToplevelOrStatement (SeekableStreamReader seekable)
		{
			Tokenizer tokenizer = new Tokenizer (seekable, (CompilationUnit) Location.SourceFiles [0]);
			
			int t = tokenizer.token ();
			switch (t){
			case Token.EOF:
				return InputKind.EOF;
				
			// These are toplevels
			case Token.EXTERN:
			case Token.OPEN_BRACKET:
			case Token.ABSTRACT:
			case Token.CLASS:
			case Token.ENUM:
			case Token.INTERFACE:
			case Token.INTERNAL:
			case Token.NAMESPACE:
			case Token.PRIVATE:
			case Token.PROTECTED:
			case Token.PUBLIC:
			case Token.SEALED:
			case Token.STATIC:
			case Token.STRUCT:
				return InputKind.CompilationUnit;
				
			// Definitely expression
			case Token.FIXED:
			case Token.BOOL:
			case Token.BYTE:
			case Token.CHAR:
			case Token.DECIMAL:
			case Token.DOUBLE:
			case Token.FLOAT:
			case Token.INT:
			case Token.LONG:
			case Token.NEW:
			case Token.OBJECT:
			case Token.SBYTE:
			case Token.SHORT:
			case Token.STRING:
			case Token.UINT:
			case Token.ULONG:
				return InputKind.StatementOrExpression;

			// These need deambiguation help
			case Token.USING:
				t = tokenizer.token ();
				if (t == Token.EOF)
					return InputKind.EOF;

				if (t == Token.IDENTIFIER)
					return InputKind.CompilationUnit;
				return InputKind.StatementOrExpression;


			// Distinguish between:
			//    delegate opt_anonymous_method_signature block
			//    delegate type 
			case Token.DELEGATE:
				t = tokenizer.token ();
				if (t == Token.EOF)
					return InputKind.EOF;
				if (t == Token.OPEN_PARENS || t == Token.OPEN_BRACE)
					return InputKind.StatementOrExpression;
				return InputKind.CompilationUnit;

			// Distinguih between:
			//    unsafe block
			//    unsafe as modifier of a type declaration
			case Token.UNSAFE:
				t = tokenizer.token ();
				if (t == Token.EOF)
					return InputKind.EOF;
				if (t == Token.OPEN_PARENS)
					return InputKind.StatementOrExpression;
				return InputKind.CompilationUnit;
				
		        // These are errors: we list explicitly what we had
			// from the grammar, ERROR and then everything else

			case Token.READONLY:
			case Token.OVERRIDE:
			case Token.ERROR:
				return InputKind.Error;

			// This catches everything else allowed by
			// expressions.  We could add one-by-one use cases
			// if needed.
			default:
				return InputKind.StatementOrExpression;
			}
		}
		
		//
		// Parses the string @input and returns a CSharpParser if succeeful.
		//
		// if @silent is set to true then no errors are
		// reported to the user.  This is used to do various calls to the
		// parser and check if the expression is parsable.
		//
		// @partial_input: if @silent is true, then it returns whether the
		// parsed expression was partial, and more data is needed
		//
		static CSharpParser ParseString (bool silent, string input, out bool partial_input)
		{
			partial_input = false;
			Reset ();
			queued_fields.Clear ();

			Stream s = new MemoryStream (Encoding.Default.GetBytes (input));
			SeekableStreamReader seekable = new SeekableStreamReader (s, Encoding.Default);

			InputKind kind = ToplevelOrStatement (seekable);
			if (kind == InputKind.Error){
				if (!silent)
					Report.Error (-25, "Detection Parsing Error");
				partial_input = false;
				return null;
			}

			if (kind == InputKind.EOF){
				if (silent == false)
					Console.Error.WriteLine ("Internal error: EOF condition should have been detected in a previous call with silent=true");
				partial_input = true;
				return null;
				
			}
			seekable.Position = 0;

			CSharpParser parser = new CSharpParser (seekable, (CompilationUnit) Location.SourceFiles [0]);
			parser.ErrorOutput = Report.Stderr;

			if (kind == InputKind.StatementOrExpression){
				parser.Lexer.putback_char = Tokenizer.EvalStatementParserCharacter;
				RootContext.StatementMode = true;
			} else {
				//
				// Do not activate EvalCompilationUnitParserCharacter until
				// I have figured out all the limitations to invoke methods
				// in the generated classes.  See repl.txt
				//
				parser.Lexer.putback_char = Tokenizer.EvalUsingDeclarationsParserCharacter;
				//parser.Lexer.putback_char = Tokenizer.EvalCompilationUnitParserCharacter;
				RootContext.StatementMode = false;
			}

			if (silent)
				Report.DisableReporting ();
			try {
				parser.parse ();
			} finally {
				if (Report.Errors != 0){
					if (silent && parser.UnexpectedEOF)
						partial_input = true;

					parser.undo.ExecuteUndo ();
					parser = null;
				}

				if (silent)
					Report.EnableReporting ();
			}
			return parser;
		}

		//
		// Queue all the fields that we use, as we need to then go from FieldBuilder to FieldInfo
		// or reflection gets confused (it basically gets confused, and variables override each
		// other).
		//
		static ArrayList queued_fields = new ArrayList ();
		
		//static ArrayList types = new ArrayList ();

		static volatile bool invoking;
		
		static CompiledMethod CompileBlock (Class host, Undo undo)
		{
			RootContext.ResolveTree ();
			if (Report.Errors != 0){
				undo.ExecuteUndo ();
				return null;
			}
			
			RootContext.PopulateTypes ();

			if (Report.Errors != 0){
				undo.ExecuteUndo ();
				return null;
			}

			TypeBuilder tb = null;
			MethodBuilder mb = null;
				
			if (host != null){
				tb = host.TypeBuilder;
				mb = null;
				foreach (MemberCore member in host.Methods){
					if (member.Name != "Host")
						continue;
					
					MethodOrOperator method = (MethodOrOperator) member;
					mb = method.MethodBuilder;
					break;
				}

				if (mb == null)
					throw new Exception ("Internal error: did not find the method builder for the generated method");
			}
			
			RootContext.EmitCode ();
			if (Report.Errors != 0)
				return null;
			
			RootContext.CloseTypes ();

			if (Environment.GetEnvironmentVariable ("SAVE") != null)
				CodeGen.Save (current_debug_name, false);

			if (host == null)
				return null;
			
			//
			// Unlike Mono, .NET requires that the MethodInfo is fetched, it cant
			// work from MethodBuilders.   Retarded, I know.
			//
			Type tt = CodeGen.Assembly.Builder.GetType (tb.Name);
			MethodInfo mi = tt.GetMethod (mb.Name);
			
			// Pull the FieldInfos from the type, and keep track of them
			foreach (Field field in queued_fields){
				FieldInfo fi = tt.GetField (field.Name);
				
				FieldInfo old = (FieldInfo) fields [field.Name];
				
				// If a previous value was set, nullify it, so that we do
				// not leak memory
				if (old != null){
					if (old.FieldType.IsValueType){
						//
						// TODO: Clear fields for structs
						//
					} else {
						try {
							old.SetValue (null, null);
						} catch {
						}
					}
				}
				
				fields [field.Name] = fi;
			}
			//types.Add (tb);

			queued_fields.Clear ();
			
			return (CompiledMethod) System.Delegate.CreateDelegate (typeof (CompiledMethod), mi);
		}
		
		static internal void LoadAliases (NamespaceEntry ns)
		{
			ns.Populate (using_alias_list, using_list);
		}
		
		/// <summary>
		///   A sentinel value used to indicate that no value was
		///   was set by the compiled function.   This is used to
		///   differentiate between a function not returning a
		///   value and null.
		/// </summary>
		public class NoValueSet {
		}

		static internal FieldInfo LookupField (string name)
		{
			FieldInfo fi =  (FieldInfo) fields [name];

			return fi;
		}

		//
		// Puts the FieldBuilder into a queue of names that will be
		// registered.   We can not register FieldBuilders directly
		// we need to fetch the FieldInfo after Reflection cooks the
		// types, or bad things happen (bad means: FieldBuilders behave
		// incorrectly across multiple assemblies, causing assignments to
		// invalid areas
		//
		// This also serves for the parser to register Field classes
		// that should be exposed as global variables
		//
		static internal void QueueField (Field f)
		{
			queued_fields.Add (f);
		}

		static string Quote (string s)
		{
			if (s.IndexOf ('"') != -1)
				s = s.Replace ("\"", "\\\"");
			
			return "\"" + s + "\"";
		}

		static public string GetUsing ()
		{
			lock (evaluator_lock){
				StringBuilder sb = new StringBuilder ();
				
				foreach (object x in using_alias_list)
					sb.Append (String.Format ("using {0};\n", x));
				
				foreach (object x in using_list)
					sb.Append (String.Format ("using {0};\n", x));
				
				return sb.ToString ();
			}
		}

		static public string GetVars ()
		{
			lock (evaluator_lock){
				StringBuilder sb = new StringBuilder ();
				
				foreach (DictionaryEntry de in fields){
					FieldInfo fi = LookupField ((string) de.Key);
					object value = null;
					bool error = false;
					
					try {
						if (value == null)
							value = "null";
						value = fi.GetValue (null);
						if (value is string)
							value = Quote ((string)value);
					} catch {
						error = true;
					}
					
					if (error)
						sb.Append (String.Format ("{0} {1} <error reading value>", TypeManager.CSharpName(fi.FieldType), de.Key));
					else
						sb.Append (String.Format ("{0} {1} = {2}", TypeManager.CSharpName(fi.FieldType), de.Key, value));
				}
				
				return sb.ToString ();
			}
		}

		/// <summary>
		///    Loads the given assembly and exposes the API to the user.
		/// </summary>
		static public void LoadAssembly (string file)
		{
			lock (evaluator_lock){
				Driver.LoadAssembly (file, false);
				RootNamespace.ComputeNamespaces ();
			}
		}

		/// <summary>
		///    Exposes the API of the given assembly to the Evaluator
		/// </summary>
		static public void ReferenceAssembly (Assembly a)
		{
			lock (evaluator_lock){
				RootNamespace.Global.AddAssemblyReference (a);
				RootNamespace.ComputeNamespaces ();
			}
		}
		
	}

	
	/// <summary>
	///   A delegate that can be used to invoke the
	///   compiled expression or statement.
	/// </summary>
	/// <remarks>
	///   Since the Compile methods will compile
	///   statements and expressions into the same
	///   delegate, you can tell if a value was returned
	///   by checking whether the returned value is of type
	///   NoValueSet.   
	/// </remarks>
	
	public delegate void CompiledMethod (ref object retvalue);

	/// <summary>
	///   The default base class for every interaction line
	/// </summary>
	/// <remarks>
	///   The expressions and statements behave as if they were
	///   a static method of this class.   The InteractiveBase class
	///   contains a number of useful methods, but can be overwritten
	///   by setting the InteractiveBaseType property in the Evaluator
	/// </remarks>
	public class InteractiveBase {
		/// <summary>
		///   Determines where the standard output of methods in this class will go. 
		/// </summary>
		public static TextWriter Output = Console.Out;

		/// <summary>
		///   Determines where the standard error of methods in this class will go. 
		/// </summary>
		public static TextWriter Error = Console.Error;

		/// <summary>
		///   The primary prompt used for interactive use.
		/// </summary>
		public static string Prompt             = "csharp> ";

		/// <summary>
		///   The secondary prompt used for interactive use (used when
		///   an expression is incomplete).
		/// </summary>
		public static string ContinuationPrompt = "      > ";

		/// <summary>
		///   Used to signal that the user has invoked the  `quit' statement.
		/// </summary>
		public static bool QuitRequested;
		
		/// <summary>
		///   Shows all the variables defined so far.
		/// </summary>
		static public void ShowVars ()
		{
			Output.Write (Evaluator.GetVars ());
			Output.Flush ();
		}

		/// <summary>
		///   Displays the using statements in effect at this point. 
		/// </summary>
		static public void ShowUsing ()
		{
			Output.Write (Evaluator.GetUsing ());
			Output.Flush ();
		}

		public delegate void Simple ();
		
		/// <summary>
		///   Times the execution of the given delegate
		/// </summary>
		static public TimeSpan Time (Simple a)
		{
			DateTime start = DateTime.Now;
			a ();
			return DateTime.Now - start;
		}
		
#if !SMCS_SOURCE
		/// <summary>
		///   Loads the assemblies from a package
		/// </summary>
		/// <remarks>
		///   Loads the assemblies from a package.   This is equivalent
		///   to passing the -pkg: command line flag to the C# compiler
		///   on the command line. 
		/// </remarks>
		static public void LoadPackage (string pkg)
		{
			if (pkg == null){
				Error.WriteLine ("Invalid package specified");
				return;
			}

			string pkgout = Driver.GetPackageFlags (pkg, false);
			if (pkgout == null)
				return;

			string [] xargs = pkgout.Trim (new Char [] {' ', '\n', '\r', '\t'}).
				Split (new Char [] { ' ', '\t'});

			foreach (string s in xargs){
				if (s.StartsWith ("-r:") || s.StartsWith ("/r:") || s.StartsWith ("/reference:")){
					string lib = s.Substring (s.IndexOf (':')+1);

					Evaluator.LoadAssembly (lib);
					continue;
				}
			}
		}
#endif

		/// <summary>
		///   Loads the assembly
		/// </summary>
		/// <remarks>
		///   Loads the specified assembly and makes its types
		///   available to the evaluator.  This is equivalent
		///   to passing the -pkg: command line flag to the C#
		///   compiler on the command line.
		/// </remarks>
		static public void LoadAssembly (string assembly)
		{
			Evaluator.LoadAssembly (assembly);
		}
		
		/// <summary>
		///   Returns a list of available static methods. 
		/// </summary>
		static public string help {
			get {
				return  "Static methods:\n"+
					"  Describe(obj)      - Describes the object's type\n" + 
					"  LoadPackage (pkg); - Loads the given Package (like -pkg:FILE)\n" +
					"  LoadAssembly (ass) - Loads the given assembly (like -r:ASS)\n" + 
					"  ShowVars ();       - Shows defined local variables.\n" +
					"  ShowUsing ();      - Show active using decltions.\n" +
					"  Prompt             - The prompt used by the C# shell\n" +
					"  ContinuationPrompt - The prompt for partial input\n" +
					"  Time(() -> { })    - Times the specified code\n" +
					"  quit;\n" +
					"  help;\n";
			}
		}

		/// <summary>
		///   Indicates to the read-eval-print-loop that the interaction should be finished. 
		/// </summary>
		static public object quit {
			get {
				QuitRequested = true;
				return null;
			}
		}

#if !NET_2_1
		/// <summary>
		///   Describes an object or a type.
		/// </summary>
		/// <remarks>
		///   This method will show a textual representation
		///   of the object's type.  If the object is a
		///   System.Type it renders the type directly,
		///   otherwise it renders the type returned by
		///   invoking GetType on the object.
		/// </remarks>
		static public string Describe (object x)
		{
			if (x == null)
				return "";
			
			Type t = x as Type;
			if (t == null)
				t = x.GetType ();

			StringWriter sw = new StringWriter ();
			new Outline (t, sw, true, false, false).OutlineType ();
			return sw.ToString ();
		}
#endif
	}

	//
	// A local variable reference that will create a Field in a
	// Class with the resolved type.  This is necessary so we can
	// support "var" as a field type in a class declaration.
	//
	// We allow LocalVariableReferece to do the heavy lifting, and
	// then we insert the field with the resolved type
	//
	public class LocalVariableReferenceWithClassSideEffect : LocalVariableReference {
		TypeContainer container;
		string name;
		
		public LocalVariableReferenceWithClassSideEffect (TypeContainer container, string name, Block current_block, string local_variable_id, Location loc)
			: base (current_block, local_variable_id, loc)
		{
			this.container = container;
			this.name = name;
		}

		public override bool Equals (object obj)
		{
			LocalVariableReferenceWithClassSideEffect lvr = obj as LocalVariableReferenceWithClassSideEffect;
			if (lvr == null)
				return false;

			if (lvr.name != name || lvr.container != container)
				return false;

			return base.Equals (obj);
		}

		public override int GetHashCode ()
		{
			return name.GetHashCode ();
		}
		
		override public Expression DoResolveLValue (EmitContext ec, Expression right_side)
		{
			Expression ret = base.DoResolveLValue (ec, right_side);
			if (ret == null)
				return null;

			Field f = new Field (container, new TypeExpression (ret.Type, Location),
					     Modifiers.PUBLIC | Modifiers.STATIC,
					     new MemberName (name, Location), null);
			container.AddField (f);
			if (f.Define ())
				Evaluator.QueueField (f);
			
			return ret;
		}
	}

	/// <summary>
	///    A class used to assign values if the source expression is not void
	///
	///    Used by the interactive shell to allow it to call this code to set
	///    the return value for an invocation.
	/// </summary>
	class OptionalAssign : SimpleAssign {
		public OptionalAssign (Expression t, Expression s, Location loc)
			: base (t, s, loc)
		{
		}

		public override Expression DoResolve (EmitContext ec)
		{
			CloneContext cc = new CloneContext ();
			Expression clone = source.Clone (cc);

			clone = clone.Resolve (ec);
			if (clone == null)
				return null;

			// This means its really a statement.
			if (clone.Type == TypeManager.void_type){
				source = source.Resolve (ec);
				target = null;
				type = TypeManager.void_type;
				eclass = ExprClass.Value;
				return this;
			}

			return base.DoResolve (ec);
		}

		public override void Emit (EmitContext ec)
		{
			if (target == null)
				source.Emit (ec);
			else
				base.Emit (ec);
		}

		public override void EmitStatement (EmitContext ec)
		{
			if (target == null)
				source.Emit (ec);
			else
				base.EmitStatement (ec);
		}
	}

	public class Undo {
		ArrayList undo_types;
		
		public Undo ()
		{
			undo_types = new ArrayList ();
		}

		public void AddTypeContainer (TypeContainer current_container, TypeContainer tc)
		{
			if (current_container == tc){
				Console.Error.WriteLine ("Internal error: inserting container into itself");
				return;
			}
			
			if (undo_types == null)
				undo_types = new ArrayList ();
			undo_types.Add (new Pair (current_container, tc));
		}

		public void ExecuteUndo ()
		{
			if (undo_types == null)
				return;

			foreach (Pair p in undo_types){
				TypeContainer current_container = (TypeContainer) p.First;

				current_container.RemoveTypeContainer ((TypeContainer) p.Second);
			}
			undo_types = null;
		}
	}
	
}
	
