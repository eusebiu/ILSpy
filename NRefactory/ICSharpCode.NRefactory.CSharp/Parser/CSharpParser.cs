﻿// 
// CSharpParser.cs
//
// Author:
//       Mike Krüger <mkrueger@novell.com>
// 
// Copyright (c) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.CSharp;
using ICSharpCode.NRefactory.TypeSystem;

namespace ICSharpCode.NRefactory.CSharp
{
	public class CSharpParser
	{
		class ConversionVisitor : StructuralVisitor
		{
			CompilationUnit unit = new CompilationUnit ();
			internal bool convertTypeSystemMode;
			
			public CompilationUnit Unit {
				get {
					return unit;
				}
				set {
					unit = value;
				}
			}
			
			public LocationsBag LocationsBag  {
				get;
				private set;
			}
			
			public ConversionVisitor (bool convertTypeSystemMode, LocationsBag locationsBag)
			{
				this.convertTypeSystemMode = convertTypeSystemMode;
				this.LocationsBag = locationsBag;
			}
			
			public static TextLocation Convert (Mono.CSharp.Location loc)
			{
				return new TextLocation (loc.Row, loc.Column);
			}
			
			
			#region Global
			Stack<NamespaceDeclaration> namespaceStack = new Stack<NamespaceDeclaration> ();
			
			void AddTypeArguments (ATypeNameExpression texpr, AstType result)
			{
				if (texpr.TypeArguments == null || texpr.TypeArguments.Args == null)
					return;
				var loc = LocationsBag.GetLocations (texpr.TypeArguments);
				if (loc != null && loc.Count >= 2)
					result.AddChild (new CSharpTokenNode (Convert (loc [loc.Count - 2]), 1), AstType.Roles.LChevron);
				int i = 0;
				foreach (var arg in texpr.TypeArguments.Args) {
					result.AddChild (ConvertToType (arg), AstType.Roles.TypeArgument);
					if (loc != null && i < loc.Count - 2)
						result.AddChild (new CSharpTokenNode (Convert (loc [i++]), 1), AstType.Roles.Comma);
				}
				if (loc != null && loc.Count >= 2)
					result.AddChild (new CSharpTokenNode (Convert (loc [loc.Count - 1]), 1), AstType.Roles.RChevron);
			}
			
			AstType ConvertToType (MemberName memberName)
			{
				AstType result;
				if (memberName.Left != null) {
					result = new MemberType ();
					result.AddChild (ConvertToType (memberName.Left), MemberType.TargetRole);
					var loc = LocationsBag.GetLocations (memberName.Left);
					if (loc != null)
						result.AddChild (new CSharpTokenNode (Convert (loc [0]), 1), MemberType.Roles.Dot);
					result.AddChild (Identifier.Create (memberName.Name, Convert (memberName.Location)), MemberType.Roles.Identifier);
				} else {
					result = new SimpleType () { IdentifierToken = Identifier.Create (memberName.Name, Convert (memberName.Location)) };
				}
				if (memberName.TypeArguments != null && !memberName.TypeArguments.IsEmpty) {
					var chevronLocs = LocationsBag.GetLocations (memberName.TypeArguments);
					if (chevronLocs != null)
						result.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 2]), 1), InvocationExpression.Roles.LChevron);
					int i = 0;
					foreach (var arg in memberName.TypeArguments.Args) {
						result.AddChild (ConvertToType (arg), AstType.Roles.TypeArgument);
						if (chevronLocs != null && i < chevronLocs.Count - 2)
							result.AddChild (new CSharpTokenNode (Convert (chevronLocs [i++]), 1), InvocationExpression.Roles.Comma);
					}
					if (chevronLocs != null)
						result.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 1]), 1), InvocationExpression.Roles.RChevron);
				}
				return result;
			}
				
			AstType ConvertToType (Mono.CSharp.Expression typeName)
			{
				if (typeName == null) // may happen in typeof(Generic<,,,,>)
					return new SimpleType ();
				
				if (typeName is TypeExpression) {
					var typeExpr = (Mono.CSharp.TypeExpression)typeName;
					return new PrimitiveType (typeExpr.GetSignatureForError (), Convert (typeExpr.Location));
				}
				
				if (typeName is Mono.CSharp.QualifiedAliasMember) {
					var qam = (Mono.CSharp.QualifiedAliasMember)typeName;
					var memberType = new MemberType (); 
					memberType.Target = new SimpleType (qam.alias, Convert (qam.Location));
					memberType.IsDoubleColon = true;
					memberType.MemberName = qam.Name;
					return memberType;
				}
				
				if (typeName is MemberAccess) {
					MemberAccess ma = (MemberAccess)typeName;
					
					var memberType = new MemberType ();
					memberType.AddChild (ConvertToType (ma.LeftExpression), MemberType.TargetRole);
					memberType.AddChild (new CSharpTokenNode (Convert (ma.DotLocation), 1), MemberType.Roles.Dot);
					
					memberType.MemberNameToken = Identifier.Create (ma.Name, Convert (ma.Location));
					
					AddTypeArguments (ma, memberType);
					return memberType;
				}
				
				if (typeName is SimpleName) {
					var sn = (SimpleName)typeName;
					var result = new SimpleType (sn.Name, Convert (sn.Location));
					AddTypeArguments (sn, result);
					return result;
				}
				
				if (typeName is ComposedCast) {
					var cc = (ComposedCast)typeName;
					var baseType = ConvertToType (cc.Left);
					var result = new ComposedType () { BaseType = baseType };
					var ccSpec = cc.Spec;
					while (ccSpec != null) {
						if (ccSpec.IsNullable) {
							result.AddChild (new CSharpTokenNode (Convert (ccSpec.Location), 1), ComposedType.NullableRole);
						} else if (ccSpec.IsPointer) {
							result.AddChild (new CSharpTokenNode (Convert (ccSpec.Location), 1), ComposedType.PointerRole);
						} else {
							var location = LocationsBag.GetLocations (ccSpec);
							var spec = new ArraySpecifier () { Dimensions = ccSpec.Dimension };
							spec.AddChild (new CSharpTokenNode (Convert (ccSpec.Location), 1), FieldDeclaration.Roles.LBracket);
							if (location != null)
								spec.AddChild (new CSharpTokenNode (Convert (location [0]), 1), FieldDeclaration.Roles.RBracket);
							
							result.ArraySpecifiers.Add (spec);
						}
						ccSpec = ccSpec.Next;
					}
					return result;
				}
				
				if (typeName is SpecialContraintExpr) {
					var sce = (SpecialContraintExpr)typeName;
					switch (sce.Constraint) {
					case SpecialConstraint.Class:
						return new PrimitiveType ("class", Convert (sce.Location));
					case SpecialConstraint.Struct:
						return new PrimitiveType ("struct", Convert (sce.Location));
					case SpecialConstraint.Constructor:
						return new PrimitiveType ("new", Convert (sce.Location));
					}
				}
				System.Console.WriteLine ("Error while converting :" + typeName + " - unknown type name");
				System.Console.WriteLine (Environment.StackTrace);
				return new SimpleType ("unknown");
			}
			
			IEnumerable<Attribute> GetAttributes (List<Mono.CSharp.Attribute> optAttributes)
			{
				if (optAttributes == null)
					yield break;
				foreach (var attr in optAttributes) {
					Attribute result = new Attribute ();
					result.Type = ConvertToType (attr.TypeNameExpression);
					var loc = LocationsBag.GetLocations (attr);
					result.HasArgumentList = loc != null;
					int pos = 0;
					if (loc != null)
						result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.LPar);
						
					if (attr.PosArguments != null) {
						foreach (var arg in attr.PosArguments) {
							var na = arg as NamedArgument;
							if (na != null) {
								var newArg = new NamedArgumentExpression ();
								newArg.AddChild (Identifier.Create (na.Name, Convert (na.Location)), NamedArgumentExpression.Roles.Identifier);
								
								var argLoc = LocationsBag.GetLocations (na);
								if (argLoc != null)
									newArg.AddChild (new CSharpTokenNode (Convert (argLoc [0]), 1), NamedArgumentExpression.Roles.Colon);
								newArg.AddChild ((Expression)na.Expr.Accept (this), NamedExpression.Roles.Expression);
								result.AddChild (newArg, Attribute.Roles.Argument);
							} else {
								result.AddChild ((Expression)arg.Expr.Accept (this), Attribute.Roles.Argument);
							}
							if (loc != null && pos + 1 < loc.Count)
								result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.Comma);
						}
					}
					if (attr.NamedArguments != null) { 
						foreach (NamedArgument na in attr.NamedArguments) {
							var newArg = new NamedExpression ();
							newArg.AddChild (Identifier.Create (na.Name, Convert (na.Location)), NamedExpression.Roles.Identifier);
							
							var argLoc = LocationsBag.GetLocations (na);
							if (argLoc != null)
								newArg.AddChild (new CSharpTokenNode (Convert (argLoc[0]), 1), NamedExpression.Roles.Assign);
							newArg.AddChild ((Expression)na.Expr.Accept (this), NamedExpression.Roles.Expression);
							result.AddChild (newArg, Attribute.Roles.Argument);
							if (loc != null && pos + 1 < loc.Count)
								result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.Comma);
						}
					}
					if (loc != null && pos < loc.Count)
						result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.RPar);
					
					yield return result;
				}
			}
			
			AttributeSection ConvertAttributeSection (List<Mono.CSharp.Attribute> optAttributes)
			{
				if (optAttributes == null)
					return null;
				AttributeSection result = new AttributeSection ();
				var loc = LocationsBag.GetLocations (optAttributes);
				int pos = 0;
				if (loc != null)
					result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.LBracket);
				
				string target = optAttributes.First ().ExplicitTarget;
				
				if (!string.IsNullOrEmpty (target)) {
					if (loc != null && pos < loc.Count - 1) {
						result.AddChild (Identifier.Create (target, Convert (loc [pos++])), AttributeSection.Roles.Identifier);
					} else {
						result.AddChild (Identifier.Create (target), AttributeSection.Roles.Identifier);
					}
					if (loc != null && pos < loc.Count)
						result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.Colon);
				}
				
				foreach (var attr in GetAttributes (optAttributes)) {
					result.AddChild (attr, AttributeSection.AttributeRole);
				}
				// optional comma
				if (loc != null && pos < loc.Count - 1 && !loc [pos].Equals (loc [pos + 1]))
					result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.Comma);
				if (loc != null && pos < loc.Count)
					result.AddChild (new CSharpTokenNode (Convert (loc [pos++]), 1), AttributeSection.Roles.RBracket);
				return result;
			}
			
			public override void Visit (UsingsBag.Namespace nspace)
			{
				NamespaceDeclaration nDecl = null;
				if (nspace.Name != null) {
					nDecl = new NamespaceDeclaration ();
					nDecl.AddChild (new CSharpTokenNode (Convert (nspace.NamespaceLocation), "namespace".Length), NamespaceDeclaration.Roles.Keyword);
					ConvertNamespaceName (nspace.Name, nDecl);
					nDecl.AddChild (new CSharpTokenNode (Convert (nspace.OpenBrace), 1), NamespaceDeclaration.Roles.LBrace);
					AddToNamespace (nDecl);
					namespaceStack.Push (nDecl);
				}
				
				VisitNamespaceUsings (nspace);
				VisitNamespaceBody (nspace);
				
				if (nDecl != null) {
					nDecl.AddChild (new CSharpTokenNode (Convert (nspace.CloseBrace), 1), NamespaceDeclaration.Roles.RBrace);
					if (!nspace.OptSemicolon.IsNull)
						nDecl.AddChild (new CSharpTokenNode (Convert (nspace.OptSemicolon), 1), NamespaceDeclaration.Roles.Semicolon);
					
					namespaceStack.Pop ();
				}
			}
			
			void ConvertNamespaceName (MemberName memberName, NamespaceDeclaration namespaceDecl)
			{
				AstNode insertPos = null;
				while (memberName != null) {
					Identifier newIdent = Identifier.Create (memberName.Name, Convert (memberName.Location));
					namespaceDecl.InsertChildBefore (insertPos, newIdent, NamespaceDeclaration.Roles.Identifier);
					insertPos = newIdent;
					
					if (!memberName.DotLocation.IsNull) {
						var dotToken = new CSharpTokenNode (Convert (memberName.DotLocation), 1);
						namespaceDecl.InsertChildBefore (insertPos, dotToken, NamespaceDeclaration.Roles.Dot);
						insertPos = dotToken;
					}
					
					memberName = memberName.Left;
				}
			}
			
			public override void Visit (UsingsBag.Using u)
			{
				UsingDeclaration ud = new UsingDeclaration ();
				ud.AddChild (new CSharpTokenNode (Convert (u.UsingLocation), "using".Length), UsingDeclaration.Roles.Keyword);
				ud.AddChild (ConvertImport (u.NSpace), UsingDeclaration.ImportRole);
				ud.AddChild (new CSharpTokenNode (Convert (u.SemicolonLocation), 1), UsingDeclaration.Roles.Semicolon);
				AddToNamespace (ud);
			}
			
			public override void Visit (UsingsBag.AliasUsing u)
			{
				UsingAliasDeclaration ud = new UsingAliasDeclaration ();
				ud.AddChild (new CSharpTokenNode (Convert (u.UsingLocation), "using".Length), UsingAliasDeclaration.Roles.Keyword);
				ud.AddChild (Identifier.Create (u.Identifier.Value, Convert (u.Identifier.Location)), UsingAliasDeclaration.AliasRole);
				ud.AddChild (new CSharpTokenNode (Convert (u.AssignLocation), 1), UsingAliasDeclaration.Roles.Assign);
				ud.AddChild (ConvertImport (u.Nspace), UsingAliasDeclaration.ImportRole);
				ud.AddChild (new CSharpTokenNode (Convert (u.SemicolonLocation), 1), UsingAliasDeclaration.Roles.Semicolon);
				AddToNamespace (ud);
			}

			public override void Visit (UsingsBag.ExternAlias u)
			{
				var ud = new ExternAliasDeclaration ();
				ud.AddChild (new CSharpTokenNode (Convert (u.ExternLocation), "extern".Length), ExternAliasDeclaration.Roles.Keyword);
				ud.AddChild (new CSharpTokenNode (Convert (u.AliasLocation), "alias".Length), ExternAliasDeclaration.AliasRole);
				ud.AddChild (Identifier.Create (u.Identifier.Value, Convert (u.Identifier.Location)), ExternAliasDeclaration.Roles.Identifier);
				ud.AddChild (new CSharpTokenNode (Convert (u.SemicolonLocation), 1), UsingAliasDeclaration.Roles.Semicolon);
				AddToNamespace (ud);
			}

			AstType ConvertImport (MemberName memberName)
			{
				if (memberName.Left != null) {
					// left.name
					var t = new MemberType();
					t.IsDoubleColon = memberName.IsDoubleColon;
					t.AddChild (ConvertImport (memberName.Left), MemberType.TargetRole);
					
					if (!memberName.DotLocation.IsNull)
						t.AddChild (new CSharpTokenNode (Convert (memberName.DotLocation), 1), MemberType.Roles.Dot);
					
					t.AddChild (Identifier.Create (memberName.Name, Convert(memberName.Location)), MemberType.Roles.Identifier);
					AddTypeArguments (t, memberName.TypeArguments);
					return t;
				} else {
					SimpleType t = new SimpleType();
					t.AddChild (Identifier.Create (memberName.Name, Convert(memberName.Location)), SimpleType.Roles.Identifier);
					AddTypeArguments (t, memberName.TypeArguments);
					return t;
				}
			}
			
			public override void Visit (MemberCore member)
			{
				Console.WriteLine ("Unknown member:");
				Console.WriteLine (member.GetType () + "-> Member {0}", member.GetSignatureForError ());
			}
			
			Stack<TypeDeclaration> typeStack = new Stack<TypeDeclaration> ();
			
			public override void Visit (Class c)
			{
				TypeDeclaration newType = new TypeDeclaration ();
				newType.ClassType = ClassType.Class;
				AddAttributeSection (newType, c);
				
				var location = LocationsBag.GetMemberLocation (c);
				AddModifiers (newType, location);
				int curLoc = 0;
				if (location != null)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), "class".Length), TypeDeclaration.Roles.Keyword);
				
				newType.AddChild (Identifier.Create (c.MemberName.Name, Convert (c.MemberName.Location)), AstNode.Roles.Identifier);
				if (c.MemberName.TypeArguments != null)  {
					AddTypeParameters (newType, c.MemberName.TypeArguments);
				}
				
				if (c.TypeBaseExpressions != null) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.Colon);
					
					var commaLocations = LocationsBag.GetLocations (c.TypeBaseExpressions);
					int i = 0;
					foreach (var baseTypes in c.TypeBaseExpressions) {
						newType.AddChild (ConvertToType (baseTypes), TypeDeclaration.BaseTypeRole);
						if (commaLocations != null && i < commaLocations.Count) {
							newType.AddChild (new CSharpTokenNode (Convert (commaLocations [i]), 1), TypeDeclaration.Roles.Comma);
							i++;
						}
					}
				}
				
				AddConstraints (newType, c);
				if (location != null && curLoc < location.Count)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.LBrace);
				typeStack.Push (newType);
				base.Visit (c);
				if (location != null && curLoc < location.Count) {
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.RBrace);
					
					if (c.HasOptionalSemicolon)
						newType.AddChild (new CSharpTokenNode (Convert (c.OptionalSemicolon), 1), AstNode.Roles.Semicolon);
					
				} else {
					// parser error, set end node to max value.
					newType.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				typeStack.Pop ();
				AddType (newType);
			}
			
			public override void Visit (Struct s)
			{
				TypeDeclaration newType = new TypeDeclaration ();
				newType.ClassType = ClassType.Struct;
				AddAttributeSection (newType, s);
				var location = LocationsBag.GetMemberLocation (s);
				AddModifiers (newType, location);
				int curLoc = 0;
				if (location != null)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), "struct".Length), TypeDeclaration.Roles.Keyword);
				newType.AddChild (Identifier.Create (s.MemberName.Name, Convert (s.MemberName.Location)), AstNode.Roles.Identifier);
				if (s.MemberName.TypeArguments != null)  {
					AddTypeParameters (newType, s.MemberName.TypeArguments);
				}
				
				if (s.TypeBaseExpressions != null) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.Colon);
					var commaLocations = LocationsBag.GetLocations (s.TypeBaseExpressions);
					int i = 0;
					foreach (var baseTypes in s.TypeBaseExpressions) {
						newType.AddChild (ConvertToType (baseTypes), TypeDeclaration.BaseTypeRole);
						if (commaLocations != null && i < commaLocations.Count) {
							newType.AddChild (new CSharpTokenNode (Convert (commaLocations [i]), 1), TypeDeclaration.Roles.Comma);
							i++;
						}
					}
				}
				
				AddConstraints (newType, s);
				if (location != null && curLoc < location.Count)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.LBrace);
				typeStack.Push (newType);
				base.Visit (s);
				if (location != null && location.Count > 2) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.RBrace);
					if (s.HasOptionalSemicolon)
						newType.AddChild (new CSharpTokenNode (Convert (s.OptionalSemicolon), 1), AstNode.Roles.Semicolon);
				} else {
					// parser error, set end node to max value.
					newType.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				typeStack.Pop ();
				AddType (newType);
			}
			
			public override void Visit (Interface i)
			{
				TypeDeclaration newType = new TypeDeclaration ();
				newType.ClassType = ClassType.Interface;
				AddAttributeSection (newType, i);
				var location = LocationsBag.GetMemberLocation (i);
				AddModifiers (newType, location);
				int curLoc = 0;
				if (location != null)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), "interface".Length), TypeDeclaration.Roles.Keyword);
				newType.AddChild (Identifier.Create (i.MemberName.Name, Convert (i.MemberName.Location)), AstNode.Roles.Identifier);
				if (i.MemberName.TypeArguments != null)  {
					AddTypeParameters (newType, i.MemberName.TypeArguments);
				}
				
				if (i.TypeBaseExpressions != null) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.Colon);
					var commaLocations = LocationsBag.GetLocations (i.TypeBaseExpressions);
					int j = 0;
					foreach (var baseTypes in i.TypeBaseExpressions) {
						newType.AddChild (ConvertToType (baseTypes), TypeDeclaration.BaseTypeRole);
						if (commaLocations != null && j < commaLocations.Count) {
							newType.AddChild (new CSharpTokenNode (Convert (commaLocations [j]), 1), TypeDeclaration.Roles.Comma);
							j++;
						}
					}
				}
				
				AddConstraints (newType, i);
				if (location != null && curLoc < location.Count)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.LBrace);
				typeStack.Push (newType);
				base.Visit (i);
				if (location != null && location.Count > 2) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.RBrace);
					if (i.HasOptionalSemicolon)
						newType.AddChild (new CSharpTokenNode (Convert (i.OptionalSemicolon), 1), AstNode.Roles.Semicolon);
				} else {
					// parser error, set end node to max value.
					newType.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				typeStack.Pop ();
				AddType (newType);
			}
			
			public override void Visit (Mono.CSharp.Delegate d)
			{
				DelegateDeclaration newDelegate = new DelegateDeclaration ();
				var location = LocationsBag.GetMemberLocation (d);
				AddAttributeSection (newDelegate, d);
				AddModifiers (newDelegate, location);
				if (location != null)
					newDelegate.AddChild (new CSharpTokenNode (Convert (location[0]), "delegate".Length), TypeDeclaration.Roles.Keyword);
				newDelegate.AddChild (ConvertToType (d.ReturnType), AstNode.Roles.Type);
				newDelegate.AddChild (Identifier.Create (d.MemberName.Name, Convert (d.MemberName.Location)), AstNode.Roles.Identifier);
				if (d.MemberName.TypeArguments != null)  {
					AddTypeParameters (newDelegate, d.MemberName.TypeArguments);
				}
				if (location != null)
					newDelegate.AddChild (new CSharpTokenNode (Convert (location[1]), 1), DelegateDeclaration.Roles.LPar);
				AddParameter (newDelegate, d.Parameters);
				
				if (location != null) {
					newDelegate.AddChild (new CSharpTokenNode (Convert (location[2]), 1), DelegateDeclaration.Roles.RPar);
				}
				AddConstraints (newDelegate, d);
				if (location != null) {
					newDelegate.AddChild (new CSharpTokenNode (Convert (location[3]), 1), DelegateDeclaration.Roles.Semicolon);
				}
				AddType (newDelegate);
			}
			
			void AddType (AttributedNode child)
			{
				if (typeStack.Count > 0) {
					typeStack.Peek ().AddChild (child, TypeDeclaration.MemberRole);
				} else {
					AddToNamespace (child);
				}
			}
			
			void AddToNamespace (AstNode child)
			{
				if (namespaceStack.Count > 0) {
					namespaceStack.Peek ().AddChild (child, NamespaceDeclaration.MemberRole);
				} else {
					unit.AddChild (child, CompilationUnit.MemberRole);
				}
			}
			
			public override void Visit (Mono.CSharp.Enum e)
			{
				TypeDeclaration newType = new TypeDeclaration ();
				AddAttributeSection (newType, e);
				newType.ClassType = ClassType.Enum;
				var location = LocationsBag.GetMemberLocation (e);
				
				AddModifiers (newType, location);
				int curLoc = 0;
				if (location != null)
					newType.AddChild (new CSharpTokenNode (Convert (location [curLoc++]), "enum".Length), TypeDeclaration.Roles.Keyword);
				newType.AddChild (Identifier.Create (e.MemberName.Name, Convert (e.MemberName.Location)), AstNode.Roles.Identifier);
				
				if (e.BaseTypeExpression != null) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.Colon);
					newType.AddChild (ConvertToType (e.BaseTypeExpression), TypeDeclaration.BaseTypeRole);
				}

				if (location != null && curLoc < location.Count)
					newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.LBrace);
				typeStack.Push (newType);
				
				foreach (EnumMember member in e.OrderedAllMembers) {
					Visit (member);
					if (location != null && curLoc < location.Count - 1) //last one is closing brace
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.Comma);
				}
				
				if (location != null && location.Count > 2) {
					if (location != null && curLoc < location.Count)
						newType.AddChild (new CSharpTokenNode (Convert (location[curLoc++]), 1), AstNode.Roles.RBrace);
					
					if (e.HasOptionalSemicolon)
						newType.AddChild (new CSharpTokenNode (Convert (e.OptionalSemicolon), 1), AstNode.Roles.Semicolon);
				} else {
					// parser error, set end node to max value.
					newType.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				typeStack.Pop ();
				AddType (newType);
			}
			
			public override void Visit (EnumMember em)
			{
				EnumMemberDeclaration newField = new EnumMemberDeclaration ();
				AddAttributeSection (newField, em);
				newField.AddChild (Identifier.Create (em.Name, Convert (em.Location)), AstNode.Roles.Identifier);
				
				if (em.Initializer != null) {
					newField.AddChild (new CSharpTokenNode (Convert (em.Initializer.Location), 1), EnumMemberDeclaration.Roles.Assign);
					newField.AddChild ((Expression)em.Initializer.Accept (this), EnumMemberDeclaration.InitializerRole);
				}
				
				typeStack.Peek ().AddChild (newField, TypeDeclaration.MemberRole);
			}
			#endregion
			
			#region Type members
			
			
			public override void Visit (FixedField f)
			{
				var location = LocationsBag.GetMemberLocation (f);
				
				var newField = new FixedFieldDeclaration ();
				AddAttributeSection (newField, f);
				AddModifiers (newField, location);
				if (location != null)
					newField.AddChild (new CSharpTokenNode (Convert (location [0]), "fixed".Length), FixedFieldDeclaration.Roles.Keyword);
				newField.AddChild (ConvertToType (f.TypeName), FixedFieldDeclaration.Roles.Type);
				
				var variable = new FixedVariableInitializer ();
				variable.AddChild (Identifier.Create (f.MemberName.Name, Convert (f.MemberName.Location)), FixedFieldDeclaration.Roles.Identifier);
				if (!f.Initializer.IsNull) {
					var bracketLocations = LocationsBag.GetLocations (f.Initializer);
					if (bracketLocations != null && bracketLocations.Count > 1)
						variable.AddChild (new CSharpTokenNode (Convert (bracketLocations [0]), 1), FixedFieldDeclaration.Roles.LBracket);
						
					variable.AddChild ((Expression)f.Initializer.Accept (this), FieldDeclaration.Roles.Expression);
					if (bracketLocations != null && bracketLocations.Count > 1)
						variable.AddChild (new CSharpTokenNode (Convert (bracketLocations [0]), 1), FixedFieldDeclaration.Roles.RBracket);
				}
				newField.AddChild (variable, FixedFieldDeclaration.VariableRole);
				
				if (f.Declarators != null) {
					foreach (var decl in f.Declarators) {
						var declLoc = LocationsBag.GetLocations (decl);
						if (declLoc != null)
							newField.AddChild (new CSharpTokenNode (Convert (declLoc [0]), 1), FieldDeclaration.Roles.Comma);
						
						variable = new FixedVariableInitializer ();
						variable.AddChild (Identifier.Create (decl.Name.Value, Convert (decl.Name.Location)), FieldDeclaration.Roles.Identifier);
						if (!decl.Initializer.IsNull) {
							var bracketLocations = LocationsBag.GetLocations (f.Initializer);
							if (bracketLocations != null && bracketLocations.Count > 1)
								variable.AddChild (new CSharpTokenNode (Convert (bracketLocations [0]), 1), FixedFieldDeclaration.Roles.LBracket);
							variable.AddChild ((Expression)decl.Initializer.Accept (this), FieldDeclaration.Roles.Expression);
							if (bracketLocations != null && bracketLocations.Count > 1)
								variable.AddChild (new CSharpTokenNode (Convert (bracketLocations [0]), 1), FixedFieldDeclaration.Roles.RBracket);
						}
						newField.AddChild (variable, FixedFieldDeclaration.VariableRole);
					}
				}
				if (location != null)
					newField.AddChild (new CSharpTokenNode (Convert (location[1]), 1), FieldDeclaration.Roles.Semicolon);
				typeStack.Peek ().AddChild (newField, TypeDeclaration.MemberRole);
				
			}

			public override void Visit (Field f)
			{
				var location = LocationsBag.GetMemberLocation (f);
				
				FieldDeclaration newField = new FieldDeclaration ();
				AddAttributeSection (newField, f);
				AddModifiers (newField, location);
				newField.AddChild (ConvertToType (f.TypeName), FieldDeclaration.Roles.Type);
				
				VariableInitializer variable = new VariableInitializer ();
				variable.AddChild (Identifier.Create (f.MemberName.Name, Convert (f.MemberName.Location)), FieldDeclaration.Roles.Identifier);
				
				if (f.Initializer != null) {
					if (location != null)
						variable.AddChild (new CSharpTokenNode (Convert (location[0]), 1), FieldDeclaration.Roles.Assign);
					variable.AddChild ((Expression)f.Initializer.Accept (this), VariableInitializer.Roles.Expression);
				}
				newField.AddChild (variable, FieldDeclaration.Roles.Variable);
				if (f.Declarators != null) {
					foreach (var decl in f.Declarators) {
						var declLoc = LocationsBag.GetLocations (decl);
						if (declLoc != null)
							newField.AddChild (new CSharpTokenNode (Convert (declLoc [0]), 1), FieldDeclaration.Roles.Comma);
						
						variable = new VariableInitializer ();
						variable.AddChild (Identifier.Create (decl.Name.Value, Convert (decl.Name.Location)), VariableInitializer.Roles.Identifier);
						if (decl.Initializer != null) {
							if (declLoc != null)
								variable.AddChild (new CSharpTokenNode (Convert (declLoc [1]), 1), FieldDeclaration.Roles.Assign);
							variable.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
						}
						newField.AddChild (variable, FieldDeclaration.Roles.Variable);
					}
				}
				if (location != null)
					newField.AddChild (new CSharpTokenNode (Convert (location[location.Count - 1]), 1), FieldDeclaration.Roles.Semicolon);

				typeStack.Peek ().AddChild (newField, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (Const f)
			{
				var location = LocationsBag.GetMemberLocation (f);
				
				FieldDeclaration newField = new FieldDeclaration ();
				AddAttributeSection (newField, f);
				AddModifiers (newField, location);
				if (location != null)
					newField.AddChild (new CSharpModifierToken (Convert (location [0]), Modifiers.Const), AttributedNode.ModifierRole);
				newField.AddChild (ConvertToType (f.TypeName), FieldDeclaration.Roles.Type);
				
				VariableInitializer variable = new VariableInitializer ();
				variable.AddChild (Identifier.Create (f.MemberName.Name, Convert (f.MemberName.Location)), VariableInitializer.Roles.Identifier);
				
				if (f.Initializer != null) {
					variable.AddChild (new CSharpTokenNode (Convert (f.Initializer.Location), 1), VariableInitializer.Roles.Assign);
					variable.AddChild ((Expression)f.Initializer.Accept (this), VariableInitializer.Roles.Expression);
				}
				newField.AddChild (variable, FieldDeclaration.Roles.Variable);
				if (f.Declarators != null) {
					foreach (var decl in f.Declarators) {
						var declLoc = LocationsBag.GetLocations (decl);
						if (declLoc != null)
							newField.AddChild (new CSharpTokenNode (Convert (declLoc[0]), 1), FieldDeclaration.Roles.Comma);
						
						variable = new VariableInitializer ();
						variable.AddChild (Identifier.Create (decl.Name.Value, Convert (decl.Name.Location)), FieldDeclaration.Roles.Identifier);
						if (decl.Initializer != null) {
							variable.AddChild (new CSharpTokenNode (Convert (decl.Initializer.Location), 1), FieldDeclaration.Roles.Assign);
							variable.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
						}
						newField.AddChild (variable, FieldDeclaration.Roles.Variable);
					}
				}
				if (location != null)
					newField.AddChild (new CSharpTokenNode (Convert (location[1]), 1), FieldDeclaration.Roles.Semicolon);
				
				typeStack.Peek ().AddChild (newField, TypeDeclaration.MemberRole);

				
			}
			
			public override void Visit (Operator o)
			{
				OperatorDeclaration newOperator = new OperatorDeclaration ();
				newOperator.OperatorType = (OperatorType)o.OperatorType;
				
				var location = LocationsBag.GetMemberLocation (o);
				AddAttributeSection (newOperator, o);
				AddModifiers (newOperator, location);
				
				
				if (o.OperatorType == Operator.OpType.Implicit) {
					if (location != null) {
						newOperator.AddChild (new CSharpTokenNode (Convert (location[0]), "implicit".Length), OperatorDeclaration.OperatorTypeRole);
						newOperator.AddChild (new CSharpTokenNode (Convert (location[1]), "operator".Length), OperatorDeclaration.OperatorKeywordRole);
					}
					newOperator.AddChild (ConvertToType (o.TypeName), AstNode.Roles.Type);
				} else if (o.OperatorType == Operator.OpType.Explicit) {
					if (location != null) {
						newOperator.AddChild (new CSharpTokenNode (Convert (location[0]), "explicit".Length), OperatorDeclaration.OperatorTypeRole);
						newOperator.AddChild (new CSharpTokenNode (Convert (location[1]), "operator".Length), OperatorDeclaration.OperatorKeywordRole);
					}
					newOperator.AddChild (ConvertToType (o.TypeName), AstNode.Roles.Type);
				} else {
					newOperator.AddChild (ConvertToType (o.TypeName), AstNode.Roles.Type);
					
					if (location != null)
						newOperator.AddChild (new CSharpTokenNode (Convert (location[0]), "operator".Length), OperatorDeclaration.OperatorKeywordRole);
					
					int opLength = OperatorDeclaration.GetToken(newOperator.OperatorType).Length;
					if (location != null)
						newOperator.AddChild (new CSharpTokenNode (Convert (location[1]), opLength), OperatorDeclaration.OperatorTypeRole);
				}
				if (location != null)
					newOperator.AddChild (new CSharpTokenNode (Convert (location[2]), 1), OperatorDeclaration.Roles.LPar);
				AddParameter (newOperator, o.ParameterInfo);
				if (location != null)
					newOperator.AddChild (new CSharpTokenNode (Convert (location[3]), 1), OperatorDeclaration.Roles.RPar);
				
				if (o.Block != null) {
					newOperator.AddChild ((BlockStatement)o.Block.Accept (this), OperatorDeclaration.Roles.Body);
				} else {
					if (location != null && location.Count >= 5)
						newOperator.AddChild (new CSharpTokenNode (Convert (location[4]), 1), MethodDeclaration.Roles.Semicolon);
				}
				typeStack.Peek ().AddChild (newOperator, TypeDeclaration.MemberRole);
			}
			
			public void AddAttributeSection (AstNode parent, Attributable a)
			{
				if (a.OptAttributes == null)
					return;
				AddAttributeSection (parent, a.OptAttributes);
			}
			
			public void AddAttributeSection (AstNode parent, Attributes attrs)
			{
				if (attrs == null)
					return;
				foreach (var attr in attrs.Sections) {
					parent.AddChild (ConvertAttributeSection (attr), AttributedNode.AttributeRole);
				}
			}
			
			public override void Visit (Indexer indexer)
			{
				IndexerDeclaration newIndexer = new IndexerDeclaration ();
				AddAttributeSection (newIndexer, indexer);
				var location = LocationsBag.GetMemberLocation (indexer);
				AddModifiers (newIndexer, location);
				newIndexer.AddChild (ConvertToType (indexer.TypeName), IndexerDeclaration.Roles.Type);
				var name = indexer.MemberName;
				if (name.Left != null) {
					newIndexer.AddChild (ConvertToType (name.Left), IndexerDeclaration.PrivateImplementationTypeRole);
					var privateImplTypeLoc = LocationsBag.GetLocations (name.Left);
					if (privateImplTypeLoc != null)
						newIndexer.AddChild (new CSharpTokenNode (Convert (privateImplTypeLoc[0]), 1), MethodDeclaration.Roles.Dot);
				}
				newIndexer.AddChild (Identifier.Create ("this", Convert (name.Location)), IndexerDeclaration.Roles.Identifier);
				
				if (location != null)
					newIndexer.AddChild (new CSharpTokenNode (Convert (location [0]), 1), IndexerDeclaration.Roles.LBracket);
				AddParameter (newIndexer, indexer.ParameterInfo);
				if (location != null)
					newIndexer.AddChild (new CSharpTokenNode (Convert (location[1]), 1), IndexerDeclaration.Roles.RBracket);
				
				if (location != null)
					newIndexer.AddChild (new CSharpTokenNode (Convert (location[2]), 1), IndexerDeclaration.Roles.LBrace);
				if (indexer.Get != null) {
					Accessor getAccessor = new Accessor ();
					var getLocation = LocationsBag.GetMemberLocation (indexer.Get);
					AddAttributeSection (getAccessor, indexer.Get);
					AddModifiers (getAccessor, getLocation);
					if (getLocation != null)
						getAccessor.AddChild (new CSharpTokenNode (Convert (indexer.Get.Location), "get".Length), PropertyDeclaration.Roles.Keyword);
					if (indexer.Get.Block != null) {
						getAccessor.AddChild ((BlockStatement)indexer.Get.Block.Accept (this), MethodDeclaration.Roles.Body);
					} else {
						if (getLocation != null && getLocation.Count > 0)
							newIndexer.AddChild (new CSharpTokenNode (Convert (getLocation[0]), 1), MethodDeclaration.Roles.Semicolon);
					}
					newIndexer.AddChild (getAccessor, PropertyDeclaration.GetterRole);
				}
				
				if (indexer.Set != null) {
					Accessor setAccessor = new Accessor ();
					var setLocation = LocationsBag.GetMemberLocation (indexer.Set);
					AddAttributeSection (setAccessor, indexer.Set);
					AddModifiers (setAccessor, setLocation);
					if (setLocation != null)
						setAccessor.AddChild (new CSharpTokenNode (Convert (indexer.Set.Location), "set".Length), PropertyDeclaration.Roles.Keyword);
					
					if (indexer.Set.Block != null) {
						setAccessor.AddChild ((BlockStatement)indexer.Set.Block.Accept (this), MethodDeclaration.Roles.Body);
					} else {
						if (setLocation != null && setLocation.Count > 0)
							newIndexer.AddChild (new CSharpTokenNode (Convert (setLocation[0]), 1), MethodDeclaration.Roles.Semicolon);
					}
					newIndexer.AddChild (setAccessor, PropertyDeclaration.SetterRole);
				}
				
				if (location != null) {
					newIndexer.AddChild (new CSharpTokenNode (Convert (location[3]), 1), IndexerDeclaration.Roles.RBrace);
				} else {
					// parser error, set end node to max value.
					newIndexer.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				typeStack.Peek ().AddChild (newIndexer, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (Method m)
			{
				MethodDeclaration newMethod = new MethodDeclaration ();
				AddAttributeSection (newMethod, m);
				var location = LocationsBag.GetMemberLocation (m);
				AddModifiers (newMethod, location);
				
				newMethod.AddChild (ConvertToType (m.TypeName), AstNode.Roles.Type);
				if (m.MethodName.Left != null) {
					newMethod.AddChild (ConvertToType (m.MethodName.Left), MethodDeclaration.PrivateImplementationTypeRole);
					var privateImplTypeLoc = LocationsBag.GetLocations (m.MethodName.Left);
					if (privateImplTypeLoc != null)
						newMethod.AddChild (new CSharpTokenNode (Convert (privateImplTypeLoc[0]), 1), MethodDeclaration.Roles.Dot);
				}
				newMethod.AddChild (Identifier.Create (m.MethodName.Name, Convert (m.Location)), AstNode.Roles.Identifier);
				
				if (m.MemberName.TypeArguments != null)  {
					AddTypeParameters (newMethod, m.MemberName.TypeArguments);
				}
				
				if (location != null)
					newMethod.AddChild (new CSharpTokenNode (Convert (location[0]), 1), MethodDeclaration.Roles.LPar);
				AddParameter (newMethod, m.ParameterInfo);
				
				if (location != null)
					newMethod.AddChild (new CSharpTokenNode (Convert (location[1]), 1), MethodDeclaration.Roles.RPar);
				
				AddConstraints (newMethod, m.GenericMethod);
				
				if (m.Block != null) {
					var bodyBlock = (BlockStatement)m.Block.Accept (this);
//					if (m.Block is ToplevelBlock) {
//						newMethod.AddChild (bodyBlock.FirstChild.NextSibling, MethodDeclaration.Roles.Body);
//					} else {
					newMethod.AddChild (bodyBlock, MethodDeclaration.Roles.Body);
//					}
				} else if (location != null) {
					if (location.Count < 3) {
						// parser error, set end node to max value.
						newMethod.AddChild (new ErrorNode (), AstNode.Roles.Error);
					} else {
						newMethod.AddChild (new CSharpTokenNode (Convert (location[2]), 1), MethodDeclaration.Roles.Semicolon);
					}
				}
				typeStack.Peek ().AddChild (newMethod, TypeDeclaration.MemberRole);
			}
			
			static Dictionary<Mono.CSharp.Modifiers, ICSharpCode.NRefactory.CSharp.Modifiers> modifierTable = new Dictionary<Mono.CSharp.Modifiers, ICSharpCode.NRefactory.CSharp.Modifiers> ();
			static string[] keywordTable;
			
			static ConversionVisitor ()
			{
				modifierTable [Mono.CSharp.Modifiers.NEW] = ICSharpCode.NRefactory.CSharp.Modifiers.New;
				modifierTable [Mono.CSharp.Modifiers.PUBLIC] = ICSharpCode.NRefactory.CSharp.Modifiers.Public;
				modifierTable [Mono.CSharp.Modifiers.PROTECTED] = ICSharpCode.NRefactory.CSharp.Modifiers.Protected;
				modifierTable [Mono.CSharp.Modifiers.PRIVATE] = ICSharpCode.NRefactory.CSharp.Modifiers.Private;
				modifierTable [Mono.CSharp.Modifiers.INTERNAL] = ICSharpCode.NRefactory.CSharp.Modifiers.Internal;
				modifierTable [Mono.CSharp.Modifiers.ABSTRACT] = ICSharpCode.NRefactory.CSharp.Modifiers.Abstract;
				modifierTable [Mono.CSharp.Modifiers.VIRTUAL] = ICSharpCode.NRefactory.CSharp.Modifiers.Virtual;
				modifierTable [Mono.CSharp.Modifiers.SEALED] = ICSharpCode.NRefactory.CSharp.Modifiers.Sealed;
				modifierTable [Mono.CSharp.Modifiers.STATIC] = ICSharpCode.NRefactory.CSharp.Modifiers.Static;
				modifierTable [Mono.CSharp.Modifiers.OVERRIDE] = ICSharpCode.NRefactory.CSharp.Modifiers.Override;
				modifierTable [Mono.CSharp.Modifiers.READONLY] = ICSharpCode.NRefactory.CSharp.Modifiers.Readonly;
				modifierTable [Mono.CSharp.Modifiers.PARTIAL] = ICSharpCode.NRefactory.CSharp.Modifiers.Partial;
				modifierTable [Mono.CSharp.Modifiers.EXTERN] = ICSharpCode.NRefactory.CSharp.Modifiers.Extern;
				modifierTable [Mono.CSharp.Modifiers.VOLATILE] = ICSharpCode.NRefactory.CSharp.Modifiers.Volatile;
				modifierTable [Mono.CSharp.Modifiers.UNSAFE] = ICSharpCode.NRefactory.CSharp.Modifiers.Unsafe;
				
				keywordTable = new string[255];
				for (int i = 0; i< keywordTable.Length; i++) 
					keywordTable [i] = "unknown";
				
				keywordTable [(int)BuiltinTypeSpec.Type.Other] = "void";
				keywordTable [(int)BuiltinTypeSpec.Type.String] = "string";
				keywordTable [(int)BuiltinTypeSpec.Type.Int] = "int";
				keywordTable [(int)BuiltinTypeSpec.Type.Object] = "object";
				keywordTable [(int)BuiltinTypeSpec.Type.Float] = "float";
				keywordTable [(int)BuiltinTypeSpec.Type.Double] = "double";
				keywordTable [(int)BuiltinTypeSpec.Type.Long] = "long";
				keywordTable [(int)BuiltinTypeSpec.Type.Byte] = "byte";
				keywordTable [(int)BuiltinTypeSpec.Type.UInt] = "uint";
				keywordTable [(int)BuiltinTypeSpec.Type.ULong] = "ulong";
				keywordTable [(int)BuiltinTypeSpec.Type.Short] = "short";
				keywordTable [(int)BuiltinTypeSpec.Type.UShort] = "ushort";
				keywordTable [(int)BuiltinTypeSpec.Type.SByte] = "sbyte";
				keywordTable [(int)BuiltinTypeSpec.Type.Decimal] = "decimal";
				keywordTable [(int)BuiltinTypeSpec.Type.Char] = "char";
			}
			
			void AddModifiers (AttributedNode parent, LocationsBag.MemberLocations location)
			{
				if (location == null || location.Modifiers == null)
					return;
				foreach (var modifier in location.Modifiers) {
					parent.AddChild (new CSharpModifierToken (Convert (modifier.Item2), modifierTable[modifier.Item1]), AttributedNode.ModifierRole);
				}
			}
			
			public override void Visit (Property p)
			{
				PropertyDeclaration newProperty = new PropertyDeclaration ();
				AddAttributeSection (newProperty, p);
				var location = LocationsBag.GetMemberLocation (p);
				AddModifiers (newProperty, location);
				newProperty.AddChild (ConvertToType (p.TypeName), AstNode.Roles.Type);
				if (p.MemberName.Left != null) {
					newProperty.AddChild (ConvertToType (p.MemberName.Left), PropertyDeclaration.PrivateImplementationTypeRole);
					var privateImplTypeLoc = LocationsBag.GetLocations (p.MemberName.Left);
					if (privateImplTypeLoc != null)
						newProperty.AddChild (new CSharpTokenNode (Convert (privateImplTypeLoc[0]), 1), MethodDeclaration.Roles.Dot);
				}
				
				newProperty.AddChild (Identifier.Create (p.MemberName.Name, Convert (p.Location)), PropertyDeclaration.Roles.Identifier);
				
				if (location != null)
					newProperty.AddChild (new CSharpTokenNode (Convert (location[0]), 1), MethodDeclaration.Roles.LBrace);
				
				Accessor getAccessor = null;
				if (p.Get != null) {
					getAccessor = new Accessor ();
					AddAttributeSection (getAccessor, p.Get);
					var getLocation = LocationsBag.GetMemberLocation (p.Get);
					AddModifiers (getAccessor, getLocation);
					getAccessor.AddChild (new CSharpTokenNode (Convert (p.Get.Location), "get".Length), PropertyDeclaration.Roles.Keyword);
					
					if (p.Get.Block != null) {
						getAccessor.AddChild ((BlockStatement)p.Get.Block.Accept (this), MethodDeclaration.Roles.Body);
					} else {
						if (getLocation != null && getLocation.Count > 0)
							getAccessor.AddChild (new CSharpTokenNode (Convert (getLocation[0]), 1), MethodDeclaration.Roles.Semicolon);
					}
				}
				
				Accessor setAccessor = null;
				if (p.Set != null) {
					setAccessor = new Accessor ();
					AddAttributeSection (setAccessor, p.Set);
					var setLocation = LocationsBag.GetMemberLocation (p.Set);
					AddModifiers (setAccessor, setLocation);
					setAccessor.AddChild (new CSharpTokenNode (Convert (p.Set.Location), "set".Length), PropertyDeclaration.Roles.Keyword);
					
					if (p.Set.Block != null) {
						setAccessor.AddChild ((BlockStatement)p.Set.Block.Accept (this), MethodDeclaration.Roles.Body);
					} else {
						if (setLocation != null && setLocation.Count > 0)
							setAccessor.AddChild (new CSharpTokenNode (Convert (setLocation[0]), 1), MethodDeclaration.Roles.Semicolon);
					}
				}
				if (getAccessor != null && setAccessor != null) {
					if (getAccessor.StartLocation < setAccessor.StartLocation) {
						newProperty.AddChild (getAccessor, PropertyDeclaration.GetterRole);
						newProperty.AddChild (setAccessor, PropertyDeclaration.SetterRole);
					} else {
						newProperty.AddChild (setAccessor, PropertyDeclaration.SetterRole);
						newProperty.AddChild (getAccessor, PropertyDeclaration.GetterRole);
					}
				} else {
					if (getAccessor != null)
						newProperty.AddChild (getAccessor, PropertyDeclaration.GetterRole);
					if (setAccessor != null)
						newProperty.AddChild (setAccessor, PropertyDeclaration.SetterRole);
				}
				
				if (location != null && location.Count > 1) {
					newProperty.AddChild (new CSharpTokenNode (Convert (location[1]), 1), MethodDeclaration.Roles.RBrace);
				} else {
					// parser error, set end node to max value.
					newProperty.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				
				typeStack.Peek ().AddChild (newProperty, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (Constructor c)
			{
				ConstructorDeclaration newConstructor = new ConstructorDeclaration ();
				AddAttributeSection (newConstructor, c);
				var location = LocationsBag.GetMemberLocation (c);
				AddModifiers (newConstructor, location);
				newConstructor.AddChild (Identifier.Create (c.MemberName.Name, Convert (c.MemberName.Location)), AstNode.Roles.Identifier);
				if (location != null)
					newConstructor.AddChild (new CSharpTokenNode (Convert (location[0]), 1), MethodDeclaration.Roles.LPar);
				
				AddParameter (newConstructor, c.ParameterInfo);
				if (location != null)
					newConstructor.AddChild (new CSharpTokenNode (Convert (location[1]), 1), MethodDeclaration.Roles.RPar);
				
				if (c.Initializer != null) {
					var initializer = new ConstructorInitializer ();
					initializer.ConstructorInitializerType = c.Initializer is ConstructorBaseInitializer ? ConstructorInitializerType.Base : ConstructorInitializerType.This;
					var initializerLocation = LocationsBag.GetLocations (c.Initializer);
					
					if (initializerLocation != null)
						newConstructor.AddChild (new CSharpTokenNode (Convert (initializerLocation[0]), 1), ConstructorDeclaration.Roles.Colon);
					// this and base has the same length
					initializer.AddChild (new CSharpTokenNode (Convert (c.Initializer.Location), "this".Length), ConstructorDeclaration.Roles.Keyword);
					if (initializerLocation != null)
						initializer.AddChild (new CSharpTokenNode (Convert (initializerLocation[1]), 1), ConstructorDeclaration.Roles.LPar);
					AddArguments (initializer, LocationsBag.GetLocations (c.Initializer.Arguments), c.Initializer.Arguments);
					if (initializerLocation != null)
						initializer.AddChild (new CSharpTokenNode (Convert (initializerLocation[2]), 1), ConstructorDeclaration.Roles.RPar);
					newConstructor.AddChild (initializer, ConstructorDeclaration.InitializerRole);
				}
				
				if (c.Block != null)
					newConstructor.AddChild ((BlockStatement)c.Block.Accept (this), ConstructorDeclaration.Roles.Body);
				
				typeStack.Peek ().AddChild (newConstructor, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (Destructor d)
			{
				DestructorDeclaration newDestructor = new DestructorDeclaration ();
				AddAttributeSection (newDestructor, d);
				var location = LocationsBag.GetMemberLocation (d);
				AddModifiers (newDestructor, location);
				if (location != null)
					newDestructor.AddChild (new CSharpTokenNode (Convert (location[0]), 1), DestructorDeclaration.TildeRole);
				newDestructor.AddChild (Identifier.Create (d.MemberName.Name, Convert (d.MemberName.Location)), AstNode.Roles.Identifier);
				
				if (location != null) {
					newDestructor.AddChild (new CSharpTokenNode (Convert (location[1]), 1), DestructorDeclaration.Roles.LPar);
					newDestructor.AddChild (new CSharpTokenNode (Convert (location[2]), 1), DestructorDeclaration.Roles.RPar);
				}
				
				if (d.Block != null)
					newDestructor.AddChild ((BlockStatement)d.Block.Accept (this), DestructorDeclaration.Roles.Body);
				
				typeStack.Peek ().AddChild (newDestructor, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (EventField e)
			{
				EventDeclaration newEvent = new EventDeclaration ();
				AddAttributeSection (newEvent, e);
				var location = LocationsBag.GetMemberLocation (e);
				AddModifiers (newEvent, location);
				
				if (location != null)
					newEvent.AddChild (new CSharpTokenNode (Convert (location[0]), "event".Length), EventDeclaration.Roles.Keyword);
				newEvent.AddChild (ConvertToType (e.TypeName), AstNode.Roles.Type);
				
				VariableInitializer variable = new VariableInitializer ();
				variable.AddChild (Identifier.Create (e.MemberName.Name, Convert (e.MemberName.Location)), FieldDeclaration.Roles.Identifier);
				
				if (e.Initializer != null) {
					if (location != null)
						variable.AddChild (new CSharpTokenNode (Convert (location[0]), 1), FieldDeclaration.Roles.Assign);
					variable.AddChild ((Expression)e.Initializer.Accept (this), VariableInitializer.Roles.Expression);
				}
				newEvent.AddChild (variable, FieldDeclaration.Roles.Variable);
				if (e.Declarators != null) {
					foreach (var decl in e.Declarators) {
						var declLoc = LocationsBag.GetLocations (decl);
						if (declLoc != null)
							newEvent.AddChild (new CSharpTokenNode (Convert (declLoc [0]), 1), FieldDeclaration.Roles.Comma);
						
						variable = new VariableInitializer ();
						variable.AddChild (Identifier.Create (decl.Name.Value, Convert (decl.Name.Location)), VariableInitializer.Roles.Identifier);

						if (decl.Initializer != null) {
							if (declLoc != null)
								variable.AddChild (new CSharpTokenNode (Convert (declLoc [1]), 1), FieldDeclaration.Roles.Assign);
							variable.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
						}
						newEvent.AddChild (variable, FieldDeclaration.Roles.Variable);
					}
				}
				
				if (location != null)
					newEvent.AddChild (new CSharpTokenNode (Convert (location[1]), ";".Length), EventDeclaration.Roles.Semicolon);
				
				typeStack.Peek ().AddChild (newEvent, TypeDeclaration.MemberRole);
			}
			
			public override void Visit (EventProperty ep)
			{
				CustomEventDeclaration newEvent = new CustomEventDeclaration ();
				AddAttributeSection (newEvent, ep);
				var location = LocationsBag.GetMemberLocation (ep);
				AddModifiers (newEvent, location);
				
				if (location != null)
					newEvent.AddChild (new CSharpTokenNode (Convert (location[0]), "event".Length), CustomEventDeclaration.Roles.Keyword);
				newEvent.AddChild (ConvertToType (ep.TypeName), CustomEventDeclaration.Roles.Type);
				if (ep.MemberName.Left != null) {
					newEvent.AddChild (ConvertToType (ep.MemberName.Left), CustomEventDeclaration.PrivateImplementationTypeRole);
					var privateImplTypeLoc = LocationsBag.GetLocations (ep.MemberName.Left);
					if (privateImplTypeLoc != null)
						newEvent.AddChild (new CSharpTokenNode (Convert (privateImplTypeLoc[0]), 1), MethodDeclaration.Roles.Dot);
				}
				
				newEvent.AddChild (Identifier.Create (ep.MemberName.Name, Convert (ep.Location)), CustomEventDeclaration.Roles.Identifier);

				if (location != null && location.Count >= 2)
					newEvent.AddChild (new CSharpTokenNode (Convert (location[1]), 1), CustomEventDeclaration.Roles.LBrace);
				
				if (ep.Add != null) {
					Accessor addAccessor = new Accessor ();
					AddAttributeSection (addAccessor, ep.Add);
					var addLocation = LocationsBag.GetMemberLocation (ep.Add);
					AddModifiers (addAccessor, addLocation);
					addAccessor.AddChild (new CSharpTokenNode (Convert (ep.Add.Location), "add".Length), CustomEventDeclaration.Roles.Keyword);
					if (ep.Add.Block != null)
						addAccessor.AddChild ((BlockStatement)ep.Add.Block.Accept (this), CustomEventDeclaration.Roles.Body);
					newEvent.AddChild (addAccessor, CustomEventDeclaration.AddAccessorRole);
				}
				
				if (ep.Remove != null) {
					Accessor removeAccessor = new Accessor ();
					AddAttributeSection (removeAccessor, ep.Remove);
					var removeLocation = LocationsBag.GetMemberLocation (ep.Remove);
					AddModifiers (removeAccessor, removeLocation);
					removeAccessor.AddChild (new CSharpTokenNode (Convert (ep.Remove.Location), "remove".Length), CustomEventDeclaration.Roles.Keyword);
					
					if (ep.Remove.Block != null)
						removeAccessor.AddChild ((BlockStatement)ep.Remove.Block.Accept (this), CustomEventDeclaration.Roles.Body);
					newEvent.AddChild (removeAccessor, CustomEventDeclaration.RemoveAccessorRole);
				}
				if (location != null && location.Count >= 3) {
					newEvent.AddChild (new CSharpTokenNode (Convert (location[2]), 1), CustomEventDeclaration.Roles.RBrace);
				} else {
					// parser error, set end node to max value.
					newEvent.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				
				typeStack.Peek ().AddChild (newEvent, TypeDeclaration.MemberRole);
			}
			
			#endregion
			
			#region Statements
			public override object Visit (Mono.CSharp.Statement stmt)
			{
				Console.WriteLine ("unknown statement:" + stmt);
				return null;
			}
			
			public override object Visit (BlockVariableDeclaration blockVariableDeclaration)
			{
				var result = new VariableDeclarationStatement ();
				result.AddChild (ConvertToType (blockVariableDeclaration.TypeExpression), VariableDeclarationStatement.Roles.Type);
				
				var varInit = new VariableInitializer ();
				var location = LocationsBag.GetLocations (blockVariableDeclaration);
				varInit.AddChild (Identifier.Create (blockVariableDeclaration.Variable.Name, Convert (blockVariableDeclaration.Variable.Location)), VariableInitializer.Roles.Identifier);
				if (blockVariableDeclaration.Initializer != null) {
					if (location != null)
						varInit.AddChild (new CSharpTokenNode (Convert (location[0]), 1), VariableInitializer.Roles.Assign);
					varInit.AddChild ((Expression)blockVariableDeclaration.Initializer.Accept (this), VariableInitializer.Roles.Expression);
				}
				
				result.AddChild (varInit, VariableDeclarationStatement.Roles.Variable);
				
				if (blockVariableDeclaration.Declarators != null) {
					foreach (var decl in blockVariableDeclaration.Declarators) {
						var loc = LocationsBag.GetLocations (decl);
						var init = new VariableInitializer ();
						if (loc != null && loc.Count > 0)
							result.AddChild (new CSharpTokenNode (Convert (loc [0]), 1), VariableInitializer.Roles.Comma);
						init.AddChild (Identifier.Create (decl.Variable.Name, Convert (decl.Variable.Location)), VariableInitializer.Roles.Identifier);
						if (decl.Initializer != null) {
							if (loc != null && loc.Count > 1)
								init.AddChild (new CSharpTokenNode (Convert (loc [1]), 1), VariableInitializer.Roles.Assign);
							init.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
						} else {
						}
						result.AddChild (init, VariableDeclarationStatement.Roles.Variable);
					}
				}
				if (location != null && (blockVariableDeclaration.Initializer == null || location.Count > 1))
					result.AddChild (new CSharpTokenNode (Convert (location[location.Count - 1]), 1), VariableDeclarationStatement.Roles.Semicolon);
				return result;
			}
			
			public override object Visit (BlockConstantDeclaration blockVariableDeclaration)
			{
				var result = new VariableDeclarationStatement ();
				
				var location = LocationsBag.GetLocations (blockVariableDeclaration);
				if (location != null)
					result.AddChild (new CSharpModifierToken (Convert (location [0]), ICSharpCode.NRefactory.CSharp.Modifiers.Const), VariableDeclarationStatement.ModifierRole);
				
				result.AddChild (ConvertToType (blockVariableDeclaration.TypeExpression), VariableDeclarationStatement.Roles.Type);
				
				var varInit = new VariableInitializer ();
				varInit.AddChild (Identifier.Create (blockVariableDeclaration.Variable.Name, Convert (blockVariableDeclaration.Variable.Location)), VariableInitializer.Roles.Identifier);
				if (blockVariableDeclaration.Initializer != null) {
					if (location != null)
						varInit.AddChild (new CSharpTokenNode (Convert (location[1]), 1), VariableInitializer.Roles.Assign);
					varInit.AddChild ((Expression)blockVariableDeclaration.Initializer.Accept (this), VariableInitializer.Roles.Expression);
				}
				
				result.AddChild (varInit, VariableDeclarationStatement.Roles.Variable);
				
				if (blockVariableDeclaration.Declarators != null) {
					foreach (var decl in blockVariableDeclaration.Declarators) {
						var loc = LocationsBag.GetLocations (decl);
						var init = new VariableInitializer ();
						init.AddChild (Identifier.Create (decl.Variable.Name, Convert (decl.Variable.Location)), VariableInitializer.Roles.Identifier);
						if (decl.Initializer != null) {
							if (loc != null)
								init.AddChild (new CSharpTokenNode (Convert (loc[0]), 1), VariableInitializer.Roles.Assign);
							init.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
							if (loc != null && loc.Count > 1)
								result.AddChild (new CSharpTokenNode (Convert (loc[1]), 1), VariableInitializer.Roles.Comma);
						} else {
							if (loc != null && loc.Count > 0)
								result.AddChild (new CSharpTokenNode (Convert (loc[0]), 1), VariableInitializer.Roles.Comma);
						}
						result.AddChild (init, VariableDeclarationStatement.Roles.Variable);
					}
				}
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[location.Count - 1]), 1), VariableDeclarationStatement.Roles.Semicolon);
				} else {
					// parser error, set end node to max value.
					result.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				return result;
			}
			
			public override object Visit (Mono.CSharp.EmptyStatement emptyStatement)
			{
				var result = new EmptyStatement ();
				result.Location = Convert (emptyStatement.loc);
				return result;
			}
			
			public override object Visit (Mono.CSharp.EmptyExpression emptyExpression)
			{
				return new ICSharpCode.NRefactory.CSharp.EmptyExpression (Convert (emptyExpression.Location));
			}
			
			public override object Visit (EmptyExpressionStatement emptyExpressionStatement)
			{
				return new EmptyExpression (Convert (emptyExpressionStatement.Location));
			}
			
			public override object Visit (If ifStatement)
			{
				var result = new IfElseStatement ();
				
				var location = LocationsBag.GetLocations (ifStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (ifStatement.loc), "if".Length), IfElseStatement.IfKeywordRole);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), IfElseStatement.Roles.LPar);
				result.AddChild ((Expression)ifStatement.Expr.Accept (this), IfElseStatement.Roles.Condition);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), IfElseStatement.Roles.RPar);
				
				if (ifStatement.TrueStatement != null)
					result.AddChild ((Statement)ifStatement.TrueStatement.Accept (this), IfElseStatement.TrueRole);
				
				if (ifStatement.FalseStatement != null) {
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location[2]), "else".Length), IfElseStatement.ElseKeywordRole);
					result.AddChild ((Statement)ifStatement.FalseStatement.Accept (this), IfElseStatement.FalseRole);
				}
				
				return result;
			}
			
			public override object Visit (Do doStatement)
			{
				var result = new DoWhileStatement ();
				var location = LocationsBag.GetLocations (doStatement);
				result.AddChild (new CSharpTokenNode (Convert (doStatement.loc), "do".Length), DoWhileStatement.DoKeywordRole);
				result.AddChild ((Statement)doStatement.EmbeddedStatement.Accept (this), WhileStatement.Roles.EmbeddedStatement);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "while".Length), DoWhileStatement.WhileKeywordRole);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), DoWhileStatement.Roles.LPar);
				result.AddChild ((Expression)doStatement.expr.Accept (this), DoWhileStatement.Roles.Condition);
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[2]), 1), DoWhileStatement.Roles.RPar);
					result.AddChild (new CSharpTokenNode (Convert (location[3]), 1), DoWhileStatement.Roles.Semicolon);
				}
				
				return result;
			}
			
			public override object Visit (While whileStatement)
			{
				var result = new WhileStatement ();
				var location = LocationsBag.GetLocations (whileStatement);
				result.AddChild (new CSharpTokenNode (Convert (whileStatement.loc), "while".Length), WhileStatement.WhileKeywordRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), WhileStatement.Roles.LPar);
				if (whileStatement.expr != null)
					result.AddChild ((Expression)whileStatement.expr.Accept (this), WhileStatement.Roles.Condition);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [1]), 1), WhileStatement.Roles.RPar);
				if (whileStatement.Statement != null)
					result.AddChild ((Statement)whileStatement.Statement.Accept (this), WhileStatement.Roles.EmbeddedStatement);
				return result;
			}
			
			void AddStatementOrList (ForStatement forStatement, Mono.CSharp.Statement init, Role<Statement> role)
			{
				if (init == null)
					return;
				if (init is StatementList) {
					foreach (var stmt in ((StatementList)init).Statements) {
						forStatement.AddChild ((Statement)stmt.Accept (this), role);
					}
				} else if (init is Mono.CSharp.EmptyStatement) {
					
				} else {
					forStatement.AddChild ((Statement)init.Accept (this), role);
				}
			}

			public override object Visit (For forStatement)
			{
				var result = new ForStatement ();
				
				var location = LocationsBag.GetLocations (forStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (forStatement.loc), "for".Length), ForStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), ForStatement.Roles.LPar);
				
				AddStatementOrList (result, forStatement.InitStatement, ForStatement.InitializerRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [1]), 1), ForStatement.Roles.Semicolon);
				if (forStatement.Test != null)
					result.AddChild ((Expression)forStatement.Test.Accept (this), ForStatement.Roles.Condition);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [2]), 1), ForStatement.Roles.Semicolon);
				
				AddStatementOrList (result, forStatement.Increment, ForStatement.IteratorRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [3]), 1), ForStatement.Roles.RPar);
				
				if (forStatement.Statement != null)
					result.AddChild ((Statement)forStatement.Statement.Accept (this), ForStatement.Roles.EmbeddedStatement);
				
				return result;
			}
			
			public override object Visit (StatementExpression statementExpression)
			{
				var result = new ExpressionStatement ();
				object expr = statementExpression.Expr.Accept (this);
				if (expr != null)
					result.AddChild ((Expression)expr, ExpressionStatement.Roles.Expression);
				var location = LocationsBag.GetLocations (statementExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ExpressionStatement.Roles.Semicolon);
				return result;
			}
			
			public override object Visit (Return returnStatement)
			{
				var result = new ReturnStatement ();
				
				result.AddChild (new CSharpTokenNode (Convert (returnStatement.loc), "return".Length), ReturnStatement.Roles.Keyword);
				if (returnStatement.Expr != null)
					result.AddChild ((Expression)returnStatement.Expr.Accept (this), ReturnStatement.Roles.Expression);
				
				var location = LocationsBag.GetLocations (returnStatement);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ReturnStatement.Roles.Semicolon);
				
				return result;
			}
			
			public override object Visit (Goto gotoStatement)
			{
				var result = new GotoStatement ();
				var location = LocationsBag.GetLocations (gotoStatement);
				result.AddChild (new CSharpTokenNode (Convert (gotoStatement.loc), "goto".Length), GotoStatement.Roles.Keyword);
				var loc = location != null ? Convert (location [0]) : TextLocation.Empty;
				result.AddChild (Identifier.Create (gotoStatement.Target, loc), GotoStatement.Roles.Identifier);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [1]), 1), GotoStatement.Roles.Semicolon);
				
				return result;
			}
			
			public override object Visit (LabeledStatement labeledStatement)
			{
				var result = new LabelStatement ();
				result.AddChild (Identifier.Create (labeledStatement.Name, Convert (labeledStatement.loc)), LabelStatement.Roles.Identifier);
				var location = LocationsBag.GetLocations (labeledStatement);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), LabelStatement.Roles.Colon);
				return result;
			}
			
			public override object Visit (GotoDefault gotoDefault)
			{
				var result = new GotoDefaultStatement ();
				result.AddChild (new CSharpTokenNode (Convert (gotoDefault.loc), "goto".Length), GotoDefaultStatement.Roles.Keyword);
				var location = LocationsBag.GetLocations (gotoDefault);
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "default".Length), GotoDefaultStatement.DefaultKeywordRole);
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), GotoDefaultStatement.Roles.Semicolon);
				}
				
				return result;
			}
			
			public override object Visit (GotoCase gotoCase)
			{
				var result = new GotoCaseStatement ();
				result.AddChild (new CSharpTokenNode (Convert (gotoCase.loc), "goto".Length), GotoCaseStatement.Roles.Keyword);
				
				var location = LocationsBag.GetLocations (gotoCase);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "case".Length), GotoCaseStatement.CaseKeywordRole);
				result.AddChild ((Expression)gotoCase.Expr.Accept (this), GotoCaseStatement.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), GotoCaseStatement.Roles.Semicolon);
				return result;
			}
			
			public override object Visit (Throw throwStatement)
			{
				var result = new ThrowStatement ();
				var location = LocationsBag.GetLocations (throwStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (throwStatement.loc), "throw".Length), ThrowStatement.Roles.Keyword);
				if (throwStatement.Expr != null)
					result.AddChild ((Expression)throwStatement.Expr.Accept (this), ThrowStatement.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ThrowStatement.Roles.Semicolon);
				return result;
			}
			
			public override object Visit (Break breakStatement)
			{
				var result = new BreakStatement ();
				var location = LocationsBag.GetLocations (breakStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (breakStatement.loc), "break".Length), BreakStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), BreakStatement.Roles.Semicolon);
				return result;
			}
			
			public override object Visit (Continue continueStatement)
			{
				var result = new ContinueStatement ();
				var location = LocationsBag.GetLocations (continueStatement);
				result.AddChild (new CSharpTokenNode (Convert (continueStatement.loc), "continue".Length), ContinueStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ContinueStatement.Roles.Semicolon);
				return result;
			}
			
			public static bool IsLower (Location left, Location right)
			{
				return left.Row < right.Row || left.Row == right.Row && left.Column < right.Column;
			}
			
			public UsingStatement CreateUsingStatement (Block blockStatement)
			{
				var usingResult = new UsingStatement ();
				Mono.CSharp.Statement cur = blockStatement.Statements[0];
				if (cur is Using) {
					Using u = (Using)cur;
					usingResult.AddChild (new CSharpTokenNode (Convert (u.loc), "using".Length), UsingStatement.Roles.Keyword);
					usingResult.AddChild (new CSharpTokenNode (Convert (blockStatement.StartLocation), 1), UsingStatement.Roles.LPar);
					if (u.Variables != null) {
						var initializer = new VariableInitializer () {
							NameToken = Identifier.Create (u.Variables.Variable.Name, Convert (u.Variables.Variable.Location)),
						};
						
						var loc = LocationsBag.GetLocations (u.Variables);
						if (loc != null)
							initializer.AddChild (new CSharpTokenNode (Convert (loc[0]), 1), VariableInitializer.Roles.Assign);
						if (u.Variables.Initializer != null)
							initializer.Initializer = u.Variables.Initializer.Accept (this) as Expression;
						
						var varDec  = new VariableDeclarationStatement () {
							Type = ConvertToType (u.Variables.TypeExpression),
							Variables = { initializer }
						};
						usingResult.AddChild (varDec, UsingStatement.ResourceAcquisitionRole);
					}
					cur = u.Statement;
					usingResult.AddChild (new CSharpTokenNode (Convert (blockStatement.EndLocation), 1), UsingStatement.Roles.RPar);
					usingResult.AddChild ((Statement)cur.Accept (this), UsingStatement.Roles.EmbeddedStatement);
				}
				return usingResult;
			}
			
			void AddBlockChildren (BlockStatement result, Block blockStatement, ref int curLocal)
			{
				if (convertTypeSystemMode) {
					return;
				}
				foreach (Mono.CSharp.Statement stmt in blockStatement.Statements) {
					if (stmt == null)
						continue;
					/*					if (curLocal < localVariables.Count && IsLower (localVariables[curLocal].Location, stmt.loc)) {
						result.AddChild (CreateVariableDeclaration (localVariables[curLocal]), AstNode.Roles.Statement);
						curLocal++;
					}*/
					if (stmt is Block && !(stmt is ToplevelBlock || stmt is ExplicitBlock)) {
						AddBlockChildren (result, (Block)stmt, ref curLocal);
					} else {
						result.AddChild ((Statement)stmt.Accept (this), BlockStatement.StatementRole);
					}
				}
			}
			
			public override object Visit (Block blockStatement)
			{
				if (blockStatement.IsCompilerGenerated && blockStatement.Statements.Any ()) {
					if (blockStatement.Statements.First () is Using)
						return CreateUsingStatement (blockStatement);
					return blockStatement.Statements.Last ().Accept (this);
				}
				var result = new BlockStatement ();
				result.AddChild (new CSharpTokenNode (Convert (blockStatement.StartLocation), 1), AstNode.Roles.LBrace);
				int curLocal = 0;
				AddBlockChildren (result, blockStatement, ref curLocal);
				
				result.AddChild (new CSharpTokenNode (Convert (blockStatement.EndLocation), 1), AstNode.Roles.RBrace);
				return result;
			}

			public override object Visit (Switch switchStatement)
			{
				var result = new SwitchStatement ();
				
				var location = LocationsBag.GetLocations (switchStatement);
				result.AddChild (new CSharpTokenNode (Convert (switchStatement.loc), "switch".Length), SwitchStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), SwitchStatement.Roles.LPar);
				if (switchStatement.Expr != null)
					result.AddChild ((Expression)switchStatement.Expr.Accept (this), SwitchStatement.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), SwitchStatement.Roles.RPar);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[2]), 1), SwitchStatement.Roles.LBrace);
				foreach (var section in switchStatement.Sections) {
					var newSection = new SwitchSection ();
					foreach (var caseLabel in section.Labels) {
						var newLabel = new CaseLabel ();
						if (caseLabel.Label != null) {
							newLabel.AddChild (new CSharpTokenNode (Convert (caseLabel.Location), "case".Length), SwitchStatement.Roles.Keyword);
							newLabel.AddChild ((Expression)caseLabel.Label.Accept (this), SwitchStatement.Roles.Expression);
							var colonLocation = LocationsBag.GetLocations (caseLabel);
							if (colonLocation != null)
								newLabel.AddChild (new CSharpTokenNode (Convert (colonLocation [0]), 1), SwitchStatement.Roles.Colon);
						} else {
							newLabel.AddChild (new CSharpTokenNode (Convert (caseLabel.Location), "default".Length), SwitchStatement.Roles.Keyword);
							newLabel.AddChild (new CSharpTokenNode (new TextLocation (caseLabel.Location.Row, caseLabel.Location.Column + "default".Length), 1), SwitchStatement.Roles.Colon);
						}
						newSection.AddChild (newLabel, SwitchSection.CaseLabelRole);
					}
					
					var blockStatement = section.Block;
					var bodyBlock = new BlockStatement ();
					int curLocal = 0;
					AddBlockChildren (bodyBlock, blockStatement, ref curLocal);
					foreach (var statement in bodyBlock.Statements) {
						statement.Remove ();
						newSection.AddChild (statement, SwitchSection.Roles.EmbeddedStatement);
						
					}
					result.AddChild (newSection, SwitchStatement.SwitchSectionRole);
				}
				
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[3]), 1), SwitchStatement.Roles.RBrace);
				} else {
					// parser error, set end node to max value.
					result.AddChild (new ErrorNode (), AstNode.Roles.Error);
				}
				
				return result;
			}
			
			public override object Visit (Lock lockStatement)
			{
				var result = new LockStatement ();
				var location = LocationsBag.GetLocations (lockStatement);
				result.AddChild (new CSharpTokenNode (Convert (lockStatement.loc), "lock".Length), LockStatement.Roles.Keyword);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), LockStatement.Roles.LPar);
				result.AddChild ((Expression)lockStatement.Expr.Accept (this), LockStatement.Roles.Expression);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), LockStatement.Roles.RPar);
				result.AddChild ((Statement)lockStatement.Statement.Accept (this), LockStatement.Roles.EmbeddedStatement);
				
				return result;
			}
			
			public override object Visit (Unchecked uncheckedStatement)
			{
				var result = new UncheckedStatement ();
				result.AddChild (new CSharpTokenNode (Convert (uncheckedStatement.loc), "unchecked".Length), UncheckedStatement.Roles.Keyword);
				result.AddChild ((BlockStatement)uncheckedStatement.Block.Accept (this), UncheckedStatement.Roles.Body);
				return result;
			}
			
			
			public override object Visit (Checked checkedStatement)
			{
				var result = new CheckedStatement ();
				result.AddChild (new CSharpTokenNode (Convert (checkedStatement.loc), "checked".Length), CheckedStatement.Roles.Keyword);
				result.AddChild ((BlockStatement)checkedStatement.Block.Accept (this), CheckedStatement.Roles.Body);
				return result;
			}
			
			public override object Visit (Unsafe unsafeStatement)
			{
				var result = new UnsafeStatement ();
				result.AddChild (new CSharpTokenNode (Convert (unsafeStatement.loc), "unsafe".Length), UnsafeStatement.Roles.Keyword);
				result.AddChild ((BlockStatement)unsafeStatement.Block.Accept (this), UnsafeStatement.Roles.Body);
				return result;
			}
			
			public override object Visit (Fixed fixedStatement)
			{
				var result = new FixedStatement ();
				var location = LocationsBag.GetLocations (fixedStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (fixedStatement.loc), "fixed".Length), FixedStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), FixedStatement.Roles.LPar);
				
				if (fixedStatement.Variables != null) {
					var blockVariableDeclaration = fixedStatement.Variables;
					result.AddChild (ConvertToType (blockVariableDeclaration.TypeExpression), FixedStatement.Roles.Type);
					var varInit = new VariableInitializer ();
					var initLocation = LocationsBag.GetLocations (blockVariableDeclaration);
					varInit.AddChild (Identifier.Create (blockVariableDeclaration.Variable.Name, Convert (blockVariableDeclaration.Variable.Location)), VariableInitializer.Roles.Identifier);
					if (blockVariableDeclaration.Initializer != null) {
						if (initLocation != null)
							varInit.AddChild (new CSharpTokenNode (Convert (location[0]), 1), VariableInitializer.Roles.Assign);
						varInit.AddChild ((Expression)blockVariableDeclaration.Initializer.Accept (this), VariableInitializer.Roles.Expression);
					}
					
					result.AddChild (varInit, FixedStatement.Roles.Variable);
					
					if (blockVariableDeclaration.Declarators != null) {
						foreach (var decl in blockVariableDeclaration.Declarators) {
							var loc = LocationsBag.GetLocations (decl);
							var init = new VariableInitializer ();
							if (loc != null && loc.Count > 0)
								result.AddChild (new CSharpTokenNode (Convert (loc [0]), 1), VariableInitializer.Roles.Comma);
							init.AddChild (Identifier.Create (decl.Variable.Name, Convert (decl.Variable.Location)), VariableInitializer.Roles.Identifier);
							if (decl.Initializer != null) {
								if (loc != null && loc.Count > 1)
									result.AddChild (new CSharpTokenNode (Convert (loc [1]), 1), VariableInitializer.Roles.Assign);
								init.AddChild ((Expression)decl.Initializer.Accept (this), VariableInitializer.Roles.Expression);
							} else {
							}
							result.AddChild (init, FixedStatement.Roles.Variable);
						}
					}
				}
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), FixedStatement.Roles.RPar);
				result.AddChild ((Statement)fixedStatement.Statement.Accept (this), FixedStatement.Roles.EmbeddedStatement);
				return result;
			}
			
			public override object Visit (TryFinally tryFinallyStatement)
			{
				TryCatchStatement result;
				var location = LocationsBag.GetLocations (tryFinallyStatement);
				
				if (tryFinallyStatement.Stmt is TryCatch) {
					result = (TryCatchStatement)tryFinallyStatement.Stmt.Accept (this);
				} else {
					result = new TryCatchStatement ();
					result.AddChild (new CSharpTokenNode (Convert (tryFinallyStatement.loc), "try".Length), TryCatchStatement.TryKeywordRole);
					if (tryFinallyStatement.Stmt != null)
						result.AddChild ((BlockStatement)tryFinallyStatement.Stmt.Accept (this), TryCatchStatement.TryBlockRole);
				}
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "finally".Length), TryCatchStatement.FinallyKeywordRole);
				result.AddChild ((BlockStatement)tryFinallyStatement.Fini.Accept (this), TryCatchStatement.FinallyBlockRole);
				
				return result;
			}
			
			CatchClause ConvertCatch (Catch ctch)
			{
				CatchClause result = new CatchClause ();
				var location = LocationsBag.GetLocations (ctch);
				result.AddChild (new CSharpTokenNode (Convert (ctch.loc), "catch".Length), CatchClause.Roles.Keyword);
				if (ctch.TypeExpression != null) {
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), CatchClause.Roles.LPar);
					
					result.AddChild (ConvertToType (ctch.TypeExpression), CatchClause.Roles.Type);
					if (ctch.Variable != null && !string.IsNullOrEmpty (ctch.Variable.Name))
						result.AddChild (Identifier.Create (ctch.Variable.Name, Convert (ctch.Variable.Location)), CatchClause.Roles.Identifier);
					
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), CatchClause.Roles.RPar);
				}

				if (ctch.Block != null)
					result.AddChild ((BlockStatement)ctch.Block.Accept (this), CatchClause.Roles.Body);
				
				return result;
			}
			
			public override object Visit (TryCatch tryCatchStatement)
			{
				var result = new TryCatchStatement ();
				result.AddChild (new CSharpTokenNode (Convert (tryCatchStatement.loc), "try".Length), TryCatchStatement.TryKeywordRole);
				if (tryCatchStatement.Block != null)
					result.AddChild ((BlockStatement)tryCatchStatement.Block.Accept (this), TryCatchStatement.TryBlockRole);
				foreach (Catch ctch in tryCatchStatement.Specific) {
					result.AddChild (ConvertCatch (ctch), TryCatchStatement.CatchClauseRole);
				}
				if (tryCatchStatement.General != null)
					result.AddChild (ConvertCatch (tryCatchStatement.General), TryCatchStatement.CatchClauseRole);
				
				return result;
			}
			
			public override object Visit (Using usingStatement)
			{
				var result = new UsingStatement ();
				var location = LocationsBag.GetLocations (usingStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (usingStatement.loc), "using".Length), UsingStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), UsingStatement.Roles.LPar);
				if (usingStatement.Expression != null)
					result.AddChild ((AstNode)usingStatement.Expression.Accept (this), UsingStatement.ResourceAcquisitionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [1]), 1), UsingStatement.Roles.RPar);
				
				if (usingStatement.Statement != null)
					result.AddChild ((Statement)usingStatement.Statement.Accept (this), UsingStatement.Roles.EmbeddedStatement);
				return result;
			}
			
			public override object Visit (Foreach foreachStatement)
			{
				var result = new ForeachStatement ();
				
				var location = LocationsBag.GetLocations (foreachStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (foreachStatement.loc), "foreach".Length), ForeachStatement.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), ForeachStatement.Roles.LPar);
				
				if (foreachStatement.TypeExpr != null)
					result.AddChild (ConvertToType (foreachStatement.TypeExpr), ForeachStatement.Roles.Type);
				if (foreachStatement.Variable != null)
					result.AddChild (Identifier.Create (foreachStatement.Variable.Name, Convert (foreachStatement.Variable.Location)), ForeachStatement.Roles.Identifier);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [1]), "in".Length), ForeachStatement.Roles.InKeyword);
				
				if (foreachStatement.Expr != null)
					result.AddChild ((Expression)foreachStatement.Expr.Accept (this), ForeachStatement.Roles.Expression);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location [2]), 1), ForeachStatement.Roles.RPar);
				if (foreachStatement.Statement != null)
					result.AddChild ((Statement)foreachStatement.Statement.Accept (this), ForeachStatement.Roles.EmbeddedStatement);
				
				return result;
			}
			
			public override object Visit (Yield yieldStatement)
			{
				var result = new YieldReturnStatement ();
				var location = LocationsBag.GetLocations (yieldStatement);
				
				result.AddChild (new CSharpTokenNode (Convert (yieldStatement.loc), "yield".Length), YieldReturnStatement.YieldKeywordRole);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "return".Length), YieldReturnStatement.ReturnKeywordRole);
				if (yieldStatement.Expr != null)
					result.AddChild ((Expression)yieldStatement.Expr.Accept (this), YieldReturnStatement.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), ";".Length), YieldReturnStatement.Roles.Semicolon);
				
				return result;
			}
			
			public override object Visit (YieldBreak yieldBreakStatement)
			{
				var result = new YieldBreakStatement ();
				var location = LocationsBag.GetLocations (yieldBreakStatement);
				result.AddChild (new CSharpTokenNode (Convert (yieldBreakStatement.loc), "yield".Length), YieldBreakStatement.YieldKeywordRole);
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "break".Length), YieldBreakStatement.BreakKeywordRole);
					result.AddChild (new CSharpTokenNode (Convert (location[1]), ";".Length), YieldBreakStatement.Roles.Semicolon);
				}
				return result;
			}
			#endregion
			
			#region Expression
			public override object Visit (Mono.CSharp.Expression expression)
			{
				Console.WriteLine ("Visit unknown expression:" + expression);
				System.Console.WriteLine (Environment.StackTrace);
				return null;
			}
			
			public override object Visit (Mono.CSharp.DefaultParameterValueExpression defaultParameterValueExpression)
			{
				return defaultParameterValueExpression.Child.Accept (this);
			}

			public override object Visit (TypeExpression typeExpression)
			{
				return new TypeReferenceExpression (new PrimitiveType (keywordTable [(int)typeExpression.Type.BuiltinType], Convert (typeExpression.Location)));
			}

			public override object Visit (LocalVariableReference localVariableReference)
			{
				return Identifier.Create (localVariableReference.Name, Convert (localVariableReference.Location));;
			}

			public override object Visit (MemberAccess memberAccess)
			{
				Expression result;
				if (memberAccess.LeftExpression is Indirection) {
					var ind = memberAccess.LeftExpression as Indirection;
					result = new PointerReferenceExpression ();
					result.AddChild ((Expression)ind.Expr.Accept (this), PointerReferenceExpression.Roles.TargetExpression);
					result.AddChild (new CSharpTokenNode (Convert (ind.Location), "->".Length), PointerReferenceExpression.ArrowRole);
				} else {
					result = new MemberReferenceExpression ();
					if (memberAccess.LeftExpression != null) {
						var leftExpr = memberAccess.LeftExpression.Accept (this);
						result.AddChild ((Expression)leftExpr, MemberReferenceExpression.Roles.TargetExpression);
					}
					if (!memberAccess.DotLocation.IsNull) {
						result.AddChild (new CSharpTokenNode (Convert (memberAccess.DotLocation), 1), MemberReferenceExpression.Roles.Dot);
					}
				}
						
				result.AddChild (Identifier.Create (memberAccess.Name, Convert (memberAccess.Location)), MemberReferenceExpression.Roles.Identifier);
				
				if (memberAccess.TypeArguments != null)  {
					AddTypeArguments (result, memberAccess.TypeArguments);
				}
				return result;
			}
			
			public override object Visit (QualifiedAliasMember qualifiedAliasMember)
			{
				var result = new MemberType ();
				result.Target = new SimpleType (qualifiedAliasMember.alias, Convert (qualifiedAliasMember.Location));
				result.IsDoubleColon = true;
				var location = LocationsBag.GetLocations (qualifiedAliasMember);
				result.AddChild (Identifier.Create (qualifiedAliasMember.Name, location != null ? Convert (location[0]) : TextLocation.Empty), MemberReferenceExpression.Roles.Identifier);
				return  new TypeReferenceExpression () { Type = result };
			}
			
			public override object Visit (Constant constant)
			{
				if (constant.GetValue () == null) 
					return new NullReferenceExpression (Convert (constant.Location));
				string literalValue;
				if (constant is ILiteralConstant) {
					literalValue = new string (((ILiteralConstant)constant).ParsedValue);
				} else {
					literalValue = constant.GetValueAsLiteral ();
				}
				object val = constant.GetValue ();
				if (val is bool)
					literalValue = (bool)val ? "true" : "false";
				var result = new PrimitiveExpression (val, Convert (constant.Location), literalValue);
				return result;
			}

			public override object Visit (SimpleName simpleName)
			{
				var result = new IdentifierExpression ();
				result.AddChild (Identifier.Create (simpleName.Name, Convert (simpleName.Location)), IdentifierExpression.Roles.Identifier);
				if (simpleName.TypeArguments != null)  {
					AddTypeArguments (result, simpleName.TypeArguments);
				}
				return result;
			}
			
			public override object Visit (BooleanExpression booleanExpression)
			{
				return booleanExpression.Expr.Accept (this);
			}

			
			public override object Visit (Mono.CSharp.ParenthesizedExpression parenthesizedExpression)
			{
				var result = new ParenthesizedExpression ();
				var location = LocationsBag.GetLocations (parenthesizedExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ParenthesizedExpression.Roles.LPar);
				result.AddChild ((Expression)parenthesizedExpression.Expr.Accept (this), ParenthesizedExpression.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), ParenthesizedExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (Unary unaryExpression)
			{
				var result = new UnaryOperatorExpression ();
				switch (unaryExpression.Oper) {
					case Unary.Operator.UnaryPlus:
						result.Operator = UnaryOperatorType.Plus;
						break;
					case Unary.Operator.UnaryNegation:
						result.Operator = UnaryOperatorType.Minus;
						break;
					case Unary.Operator.LogicalNot:
						result.Operator = UnaryOperatorType.Not;
						break;
					case Unary.Operator.OnesComplement:
						result.Operator = UnaryOperatorType.BitNot;
						break;
					case Unary.Operator.AddressOf:
						result.Operator = UnaryOperatorType.AddressOf;
						break;
				}
				result.AddChild (new CSharpTokenNode (Convert (unaryExpression.Location), 1), UnaryOperatorExpression.OperatorRole);
				result.AddChild ((Expression)unaryExpression.Expr.Accept (this), UnaryOperatorExpression.Roles.Expression);
				return result;
			}
			
			public override object Visit (UnaryMutator unaryMutatorExpression)
			{
				var result = new UnaryOperatorExpression ();
				
				var expression = (Expression)unaryMutatorExpression.Expr.Accept (this);
				switch (unaryMutatorExpression.UnaryMutatorMode) {
					case UnaryMutator.Mode.PostDecrement:
						result.Operator = UnaryOperatorType.PostDecrement;
						result.AddChild (expression, UnaryOperatorExpression.Roles.Expression);
						result.AddChild (new CSharpTokenNode (Convert (unaryMutatorExpression.Location), 2), UnaryOperatorExpression.OperatorRole);
						break;
					case UnaryMutator.Mode.PostIncrement:
						result.Operator = UnaryOperatorType.PostIncrement;
						result.AddChild (expression, UnaryOperatorExpression.Roles.Expression);
						result.AddChild (new CSharpTokenNode (Convert (unaryMutatorExpression.Location), 2), UnaryOperatorExpression.OperatorRole);
						break;
						
					case UnaryMutator.Mode.PreIncrement:
						result.Operator = UnaryOperatorType.Increment;
						result.AddChild (new CSharpTokenNode (Convert (unaryMutatorExpression.Location), 2), UnaryOperatorExpression.OperatorRole);
						result.AddChild (expression, UnaryOperatorExpression.Roles.Expression);
						break;
					case UnaryMutator.Mode.PreDecrement:
						result.Operator = UnaryOperatorType.Decrement;
						result.AddChild (new CSharpTokenNode (Convert (unaryMutatorExpression.Location), 2), UnaryOperatorExpression.OperatorRole);
						result.AddChild (expression, UnaryOperatorExpression.Roles.Expression);
						break;
				}
				
				return result;
			}
			
			public override object Visit (Indirection indirectionExpression)
			{
				var result = new UnaryOperatorExpression ();
				result.Operator = UnaryOperatorType.Dereference;
				var location = LocationsBag.GetLocations (indirectionExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 2), UnaryOperatorExpression.OperatorRole);
				result.AddChild ((Expression)indirectionExpression.Expr.Accept (this), UnaryOperatorExpression.Roles.Expression);
				return result;
			}
			
			public override object Visit (Is isExpression)
			{
				var result = new IsExpression ();
				result.AddChild ((Expression)isExpression.Expr.Accept (this), IsExpression.Roles.Expression);
				result.AddChild (new CSharpTokenNode (Convert (isExpression.Location), "is".Length), IsExpression.Roles.Keyword);
				result.AddChild (ConvertToType (isExpression.ProbeType), IsExpression.Roles.Type);
				return result;
			}
			
			public override object Visit (As asExpression)
			{
				var result = new AsExpression ();
				result.AddChild ((Expression)asExpression.Expr.Accept (this), AsExpression.Roles.Expression);
				result.AddChild (new CSharpTokenNode (Convert (asExpression.Location), "as".Length), AsExpression.Roles.Keyword);
				result.AddChild (ConvertToType (asExpression.ProbeType), AsExpression.Roles.Type);
				return result;
			}
			
			public override object Visit (Cast castExpression)
			{
				var result = new CastExpression ();
				var location = LocationsBag.GetLocations (castExpression);
				
				result.AddChild (new CSharpTokenNode (Convert (castExpression.Location), 1), CastExpression.Roles.LPar);
				if (castExpression.TargetType != null)
					result.AddChild (ConvertToType (castExpression.TargetType), CastExpression.Roles.Type);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), CastExpression.Roles.RPar);
				if (castExpression.Expr != null)
					result.AddChild ((Expression)castExpression.Expr.Accept (this), CastExpression.Roles.Expression);
				return result;
			}
			
			public override object Visit (ComposedCast composedCast)
			{
				var result = new ComposedType ();
				result.AddChild (ConvertToType (composedCast.Left), ComposedType.Roles.Type);
				
				var spec = composedCast.Spec;
				while (spec != null) {
					if (spec.IsNullable) {
						result.AddChild (new CSharpTokenNode (Convert (spec.Location), 1), ComposedType.NullableRole);
					} else if (spec.IsPointer) {
						result.AddChild (new CSharpTokenNode (Convert (spec.Location), 1), ComposedType.PointerRole);
					} else {
						var aSpec = new ArraySpecifier ();
						aSpec.AddChild (new CSharpTokenNode (Convert (spec.Location), 1), ComposedType.Roles.LBracket);
						var location = LocationsBag.GetLocations (spec);
						if (location != null)
							aSpec.AddChild (new CSharpTokenNode (Convert (spec.Location), 1), ComposedType.Roles.RBracket);
						result.AddChild (aSpec, ComposedType.ArraySpecifierRole);
					}
					spec = spec.Next;
				}
				
				return result;
			}
			
			public override object Visit (Mono.CSharp.DefaultValueExpression defaultValueExpression)
			{
				var result = new DefaultValueExpression ();
				result.AddChild (new CSharpTokenNode (Convert (defaultValueExpression.Location), "default".Length), CastExpression.Roles.Keyword);
				var location = LocationsBag.GetLocations (defaultValueExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), CastExpression.Roles.LPar);
				result.AddChild (ConvertToType (defaultValueExpression.Expr), CastExpression.Roles.Type);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), CastExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (Binary binaryExpression)
			{
				var result = new BinaryOperatorExpression ();
				int opLength = 1;
				switch (binaryExpression.Oper) {
					case Binary.Operator.Multiply:
						result.Operator = BinaryOperatorType.Multiply;
						break;
					case Binary.Operator.Division:
						result.Operator = BinaryOperatorType.Divide;
						break;
					case Binary.Operator.Modulus:
						result.Operator = BinaryOperatorType.Modulus;
						break;
					case Binary.Operator.Addition:
						result.Operator = BinaryOperatorType.Add;
						break;
					case Binary.Operator.Subtraction:
						result.Operator = BinaryOperatorType.Subtract;
						break;
					case Binary.Operator.LeftShift:
						result.Operator = BinaryOperatorType.ShiftLeft;
						opLength = 2;
						break;
					case Binary.Operator.RightShift:
						result.Operator = BinaryOperatorType.ShiftRight;
						opLength = 2;
						break;
					case Binary.Operator.LessThan:
						result.Operator = BinaryOperatorType.LessThan;
						break;
					case Binary.Operator.GreaterThan:
						result.Operator = BinaryOperatorType.GreaterThan;
						break;
					case Binary.Operator.LessThanOrEqual:
						result.Operator = BinaryOperatorType.LessThanOrEqual;
						opLength = 2;
						break;
					case Binary.Operator.GreaterThanOrEqual:
						result.Operator = BinaryOperatorType.GreaterThanOrEqual;
						opLength = 2;
						break;
					case Binary.Operator.Equality:
						result.Operator = BinaryOperatorType.Equality;
						opLength = 2;
						break;
					case Binary.Operator.Inequality:
						result.Operator = BinaryOperatorType.InEquality;
						opLength = 2;
						break;
					case Binary.Operator.BitwiseAnd:
						result.Operator = BinaryOperatorType.BitwiseAnd;
						break;
					case Binary.Operator.ExclusiveOr:
						result.Operator = BinaryOperatorType.ExclusiveOr;
						break;
					case Binary.Operator.BitwiseOr:
						result.Operator = BinaryOperatorType.BitwiseOr;
						break;
					case Binary.Operator.LogicalAnd:
						result.Operator = BinaryOperatorType.ConditionalAnd;
						opLength = 2;
						break;
					case Binary.Operator.LogicalOr:
						result.Operator = BinaryOperatorType.ConditionalOr;
						opLength = 2;
						break;
				}
				
				result.AddChild ((Expression)binaryExpression.Left.Accept (this), BinaryOperatorExpression.LeftRole);
				result.AddChild (new CSharpTokenNode (Convert (binaryExpression.Location), opLength), BinaryOperatorExpression.OperatorRole);
				result.AddChild ((Expression)binaryExpression.Right.Accept (this), BinaryOperatorExpression.RightRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Nullable.NullCoalescingOperator nullCoalescingOperator)
			{
				var result = new BinaryOperatorExpression ();
				result.Operator = BinaryOperatorType.NullCoalescing;
				result.AddChild ((Expression)nullCoalescingOperator.Left.Accept (this), BinaryOperatorExpression.LeftRole);
				result.AddChild (new CSharpTokenNode (Convert (nullCoalescingOperator.Location), 2), BinaryOperatorExpression.OperatorRole);
				result.AddChild ((Expression)nullCoalescingOperator.Right.Accept (this), BinaryOperatorExpression.RightRole);
				return result;
			}
			
			public override object Visit (Conditional conditionalExpression)
			{
				var result = new ConditionalExpression ();
				
				result.AddChild ((Expression)conditionalExpression.Expr.Accept (this), ConditionalExpression.Roles.Condition);
				var location = LocationsBag.GetLocations (conditionalExpression);
				
				result.AddChild (new CSharpTokenNode (Convert (conditionalExpression.Location), 1), ConditionalExpression.QuestionMarkRole);
				result.AddChild ((Expression)conditionalExpression.TrueExpr.Accept (this), ConditionalExpression.TrueRole);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ConditionalExpression.ColonRole);
				result.AddChild ((Expression)conditionalExpression.FalseExpr.Accept (this), ConditionalExpression.FalseRole);
				return result;
			}
			
			void AddParameter (AstNode parent, Mono.CSharp.AParametersCollection parameters)
			{
				if (parameters == null)
					return;
				var paramLocation = LocationsBag.GetLocations (parameters);
				
				for (int i = 0; i < parameters.Count; i++) {
					var p = (Parameter)parameters.FixedParameters [i];
					var location = LocationsBag.GetLocations (p);
					ParameterDeclaration parameterDeclarationExpression = new ParameterDeclaration ();
					AddAttributeSection (parameterDeclarationExpression, p);
					switch (p.ModFlags) {
					case Parameter.Modifier.OUT:
						parameterDeclarationExpression.ParameterModifier = ParameterModifier.Out;
						if (location != null)
							parameterDeclarationExpression.AddChild (new CSharpTokenNode (Convert (location [0]), "out".Length), ParameterDeclaration.Roles.Keyword);
						break;
					case Parameter.Modifier.REF:
						parameterDeclarationExpression.ParameterModifier = ParameterModifier.Ref;
						if (location != null)
							parameterDeclarationExpression.AddChild (new CSharpTokenNode (Convert (location [0]), "ref".Length), ParameterDeclaration.Roles.Keyword);
						break;
					case Parameter.Modifier.PARAMS:
						parameterDeclarationExpression.ParameterModifier = ParameterModifier.Params;
						if (location != null)
							parameterDeclarationExpression.AddChild (new CSharpTokenNode (Convert (location [0]), "params".Length), ParameterDeclaration.Roles.Keyword);
						break;
					default:
						if (p.HasExtensionMethodModifier) {
							parameterDeclarationExpression.ParameterModifier = ParameterModifier.This;
							if (location != null) {
								parameterDeclarationExpression.AddChild (new CSharpTokenNode (Convert (location [0]), "this".Length), ParameterDeclaration.Roles.Keyword);
							}
						}
						break;
					}
					if (p.TypeExpression != null) // lambdas may have no types (a, b) => ...
						parameterDeclarationExpression.AddChild (ConvertToType (p.TypeExpression), ParameterDeclaration.Roles.Type);
					if (p.Name != null)
						parameterDeclarationExpression.AddChild (Identifier.Create (p.Name, Convert (p.Location)), ParameterDeclaration.Roles.Identifier);
					if (p.HasDefaultValue) {
						if (location != null)
							parameterDeclarationExpression.AddChild (new CSharpTokenNode (Convert (location [1]), 1), ParameterDeclaration.Roles.Assign);
						parameterDeclarationExpression.AddChild ((Expression)p.DefaultValue.Accept (this), ParameterDeclaration.Roles.Expression);
					}
					parent.AddChild (parameterDeclarationExpression, InvocationExpression.Roles.Parameter);
					if (paramLocation != null && i < paramLocation.Count) {
						parent.AddChild (new CSharpTokenNode (Convert (paramLocation [i]), 1), ParameterDeclaration.Roles.Comma);
					}
				}
			}
			
			void AddTypeParameters (AstNode parent, Mono.CSharp.TypeArguments typeArguments)
			{
				if (typeArguments == null || typeArguments.IsEmpty)
					return;
				var chevronLocs = LocationsBag.GetLocations (typeArguments);
				if (chevronLocs != null)
					parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 2]), 1), InvocationExpression.Roles.LChevron);
				for (int i = 0; i < typeArguments.Count; i++) {
					if (chevronLocs != null && i > 0 && i - 1 < chevronLocs.Count)
						parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[i - 1]), 1), InvocationExpression.Roles.Comma);
					var arg = (TypeParameterName)typeArguments.Args[i];
					if (arg == null)
						continue;
					TypeParameterDeclaration tp = new TypeParameterDeclaration();
					
					List<Location> varianceLocation;
					switch (arg.Variance) {
					case Variance.Contravariant:
						tp.Variance = VarianceModifier.Contravariant;
						varianceLocation = LocationsBag.GetLocations (arg);
						if (varianceLocation != null)
							tp.AddChild (new CSharpTokenNode (Convert (varianceLocation[0]), "out".Length), TypeParameterDeclaration.VarianceRole);
						break;
					case Variance.Covariant:
						tp.Variance = VarianceModifier.Covariant;
						varianceLocation = LocationsBag.GetLocations (arg);
						if (varianceLocation != null)
							tp.AddChild (new CSharpTokenNode (Convert (varianceLocation[0]), "out".Length), TypeParameterDeclaration.VarianceRole);
						break;
					default:
						tp.Variance = VarianceModifier.Invariant;
						break;
						
					}
					
					AddAttributeSection (tp, arg.OptAttributes);

					switch (arg.Variance) {
						case Variance.Covariant:
							tp.Variance = VarianceModifier.Covariant;
							break;
						case Variance.Contravariant:
							tp.Variance = VarianceModifier.Contravariant;
							break;
					}
					tp.AddChild (Identifier.Create (arg.Name, Convert (arg.Location)), InvocationExpression.Roles.Identifier);
					parent.AddChild (tp, InvocationExpression.Roles.TypeParameter);
				}
				if (chevronLocs != null)
					parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 1]), 1), InvocationExpression.Roles.RChevron);
			}
			
			void AddTypeArguments (AstNode parent, Mono.CSharp.TypeArguments typeArguments)
			{
				if (typeArguments == null || typeArguments.IsEmpty)
					return;
				var chevronLocs = LocationsBag.GetLocations (typeArguments);
				if (chevronLocs != null)
					parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 2]), 1), InvocationExpression.Roles.LChevron);
				
				for (int i = 0; i < typeArguments.Count; i++) {
					var arg = typeArguments.Args[i];
					if (arg == null)
						continue;
					parent.AddChild (ConvertToType (arg), InvocationExpression.Roles.TypeArgument);
					if (chevronLocs != null && i < chevronLocs.Count - 2)
						parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[i]), 1), InvocationExpression.Roles.Comma);
				}
				
				if (chevronLocs != null)
					parent.AddChild (new CSharpTokenNode (Convert (chevronLocs[chevronLocs.Count - 1]), 1), InvocationExpression.Roles.RChevron);
			}
			
			void AddConstraints (AstNode parent, DeclSpace d)
			{
				if (d == null || d.Constraints == null)
					return;
				for (int i = 0; i < d.PlainConstraints.Count; i++) {
					Constraints c = d.PlainConstraints [i];
					var location = LocationsBag.GetLocations (c);
					var constraint = new Constraint ();
					constraint.AddChild (new CSharpTokenNode (Convert (c.Location), "where".Length), InvocationExpression.Roles.Keyword);
					constraint.AddChild (Identifier.Create (c.TypeParameter.Value, Convert (c.TypeParameter.Location)), InvocationExpression.Roles.Identifier);
					if (location != null)
						constraint.AddChild (new CSharpTokenNode (Convert (location [0]), 1), Constraint.ColonRole);
					var commaLocs = LocationsBag.GetLocations (c.ConstraintExpressions);
					int curComma = 0;
					foreach (var expr in c.ConstraintExpressions) {
						constraint.AddChild (ConvertToType (expr), Constraint.BaseTypeRole);
						if (commaLocs != null && curComma < commaLocs.Count)
							constraint.AddChild (new CSharpTokenNode (Convert (commaLocs[curComma++]), 1), InvocationExpression.Roles.Comma);
					}
					parent.AddChild (constraint, AstNode.Roles.Constraint);
				}
			}
			
			Expression ConvertArgument (Argument arg)
			{
				if (arg is NamedArgument) {
					var na = (NamedArgument)arg;
					NamedArgumentExpression newArg = new NamedArgumentExpression();
					newArg.AddChild (Identifier.Create (na.Name, Convert (na.Location)), NamedArgumentExpression.Roles.Identifier);
					
					var loc = LocationsBag.GetLocations (na);
					if (loc != null)
						newArg.AddChild (new CSharpTokenNode (Convert (loc[0]), 1), NamedArgumentExpression.Roles.Colon);
					
					if (arg.ArgType == Argument.AType.Out || arg.ArgType == Argument.AType.Ref) {
						DirectionExpression direction = new DirectionExpression ();
						direction.FieldDirection = arg.ArgType == Argument.AType.Out ? FieldDirection.Out : FieldDirection.Ref;
						var argLocation = LocationsBag.GetLocations (arg);
						if (argLocation != null)
							direction.AddChild (new CSharpTokenNode (Convert (argLocation[0]), "123".Length), InvocationExpression.Roles.Keyword);
						direction.AddChild ((Expression)arg.Expr.Accept (this), InvocationExpression.Roles.Expression);
						newArg.AddChild (direction, NamedArgumentExpression.Roles.Expression);
					} else {
						newArg.AddChild ((Expression)na.Expr.Accept (this), NamedArgumentExpression.Roles.Expression);
					}
					return newArg;
				}
				
				if (arg.ArgType == Argument.AType.Out || arg.ArgType == Argument.AType.Ref) {
					DirectionExpression direction = new DirectionExpression ();
					direction.FieldDirection = arg.ArgType == Argument.AType.Out ? FieldDirection.Out : FieldDirection.Ref;
					var argLocation = LocationsBag.GetLocations (arg);
					if (argLocation != null)
						direction.AddChild (new CSharpTokenNode (Convert (argLocation[0]), "123".Length), InvocationExpression.Roles.Keyword);
					direction.AddChild ((Expression)arg.Expr.Accept (this), InvocationExpression.Roles.Expression);
					return direction;
				}
				
				return (Expression)arg.Expr.Accept (this);
			}
			
			void AddArguments (AstNode parent, object location, Mono.CSharp.Arguments args)
			{
				if (args == null)
					return;
				
				var commaLocations = LocationsBag.GetLocations (args);
				for (int i = 0; i < args.Count; i++) {
					parent.AddChild (ConvertArgument (args[i]), InvocationExpression.Roles.Argument);
					if (commaLocations != null && i < commaLocations.Count) {
						parent.AddChild (new CSharpTokenNode (Convert (commaLocations[i]), 1), InvocationExpression.Roles.Comma);
					}
				}
				if (commaLocations != null && commaLocations.Count > args.Count)
					parent.AddChild (new CSharpTokenNode (Convert (commaLocations[args.Count]), 1), InvocationExpression.Roles.Comma);
			}
			
			public override object Visit (Invocation invocationExpression)
			{
				var result = new InvocationExpression ();
				var location = LocationsBag.GetLocations (invocationExpression);
				if (invocationExpression.Expression != null)
					result.AddChild ((Expression)invocationExpression.Expression.Accept (this), InvocationExpression.Roles.TargetExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), InvocationExpression.Roles.LPar);
				AddArguments (result, location, invocationExpression.Arguments);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), InvocationExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (New newExpression)
			{
				var result = new ObjectCreateExpression ();
				var location = LocationsBag.GetLocations (newExpression);
				result.AddChild (new CSharpTokenNode (Convert (newExpression.Location), "new".Length), ObjectCreateExpression.Roles.Keyword);
				
				if (newExpression.TypeRequested != null)
					result.AddChild (ConvertToType (newExpression.TypeRequested), ObjectCreateExpression.Roles.Type);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ObjectCreateExpression.Roles.LPar);
				AddArguments (result, location, newExpression.Arguments);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), ObjectCreateExpression.Roles.RPar);
				
				return result;
			}
			
			public override object Visit (NewAnonymousType newAnonymousType)
			{
				var result = new AnonymousTypeCreateExpression ();
				if (newAnonymousType.Parameters == null)
					return result;
				foreach (var par in newAnonymousType.Parameters) {
					var location = LocationsBag.GetLocations (par);

					if (location == null) {
						if (par.Expr != null)
							result.AddChild ((Expression)par.Expr.Accept (this), AnonymousTypeCreateExpression.Roles.Expression);
					} else {
						var namedExpression = new NamedExpression ();
						namedExpression.AddChild (Identifier.Create (par.Name, Convert (par.Location)), AnonymousTypeCreateExpression.Roles.Identifier);
						namedExpression.AddChild (new CSharpTokenNode (Convert (location[0]), 1), AnonymousTypeCreateExpression.Roles.Assign);
						if (par.Expr != null)
							namedExpression.AddChild ((Expression)par.Expr.Accept (this), AnonymousTypeCreateExpression.Roles.Expression);
						result.AddChild (namedExpression, AnonymousTypeCreateExpression.Roles.Expression);
					}
				}
				return result;
			}
			
			ArrayInitializerExpression ConvertCollectionOrObjectInitializers (CollectionOrObjectInitializers minit)
			{
				if (minit == null)
					return null;
				var init = new ArrayInitializerExpression ();
				AddConvertCollectionOrObjectInitializers (init, minit);
				return init;
			}
			
			void AddConvertCollectionOrObjectInitializers (Expression init, CollectionOrObjectInitializers minit)
			{
				var initLoc = LocationsBag.GetLocations (minit);
				var commaLoc = LocationsBag.GetLocations (minit.Initializers);
				int curComma = 0;
				if (initLoc != null)
					init.AddChild (new CSharpTokenNode (Convert (initLoc [0]), 1), ArrayInitializerExpression.Roles.LBrace);
				foreach (var expr in minit.Initializers) {
					var collectionInit = expr as CollectionElementInitializer;
					if (collectionInit != null) {
						var parent = new ArrayInitializerExpression ();
						
						var braceLocs = LocationsBag.GetLocations (expr);
						if (braceLocs != null)
							parent.AddChild (new CSharpTokenNode (Convert (braceLocs [0]), 1), ArrayInitializerExpression.Roles.LBrace);
						
						for (int i = 0; i < collectionInit.Arguments.Count; i++) {
							var arg = collectionInit.Arguments [i] as CollectionElementInitializer.ElementInitializerArgument;
							if (arg == null)
								continue;
							parent.AddChild ((ICSharpCode.NRefactory.CSharp.Expression)arg.Expr.Accept (this), ArrayInitializerExpression.Roles.Expression);
						}
						
						if (braceLocs != null)
							parent.AddChild (new CSharpTokenNode (Convert (braceLocs [1]), 1), ArrayInitializerExpression.Roles.RBrace);
							
						init.AddChild (parent, ArrayInitializerExpression.Roles.Expression);
					} else {
						var eleInit = expr as ElementInitializer;
						if (eleInit != null) {
							var nexpr = new NamedExpression ();
							nexpr.AddChild (Identifier.Create (eleInit.Name, Convert (eleInit.Location)), NamedArgumentExpression.Roles.Identifier);
							var assignLoc = LocationsBag.GetLocations (eleInit);
							if (assignLoc != null)
								nexpr.AddChild (new CSharpTokenNode (Convert (assignLoc [0]), 1), NamedArgumentExpression.Roles.Assign);
							if (eleInit.Source != null) {
								if (eleInit.Source is CollectionOrObjectInitializers) {
									var arrInit = new ArrayInitializerExpression ();
									AddConvertCollectionOrObjectInitializers (arrInit, eleInit.Source as CollectionOrObjectInitializers);
									nexpr.AddChild (arrInit, NamedArgumentExpression.Roles.Expression);
								} else {
									nexpr.AddChild ((Expression)eleInit.Source.Accept (this), NamedArgumentExpression.Roles.Expression);
								}
							}
							
							init.AddChild (nexpr, ArrayInitializerExpression.Roles.Expression);
						}
					}
					if (commaLoc != null && curComma < commaLoc.Count)
						init.AddChild (new CSharpTokenNode (Convert (commaLoc [curComma++]), 1), ArrayInitializerExpression.Roles.Comma);
					
				}
				if (initLoc != null) {
					if (initLoc.Count == 3) // optional comma
						init.AddChild (new CSharpTokenNode (Convert (initLoc [1]), 1), ArrayInitializerExpression.Roles.Comma);
					init.AddChild (new CSharpTokenNode (Convert (initLoc [initLoc.Count - 1]), 1), ArrayInitializerExpression.Roles.RBrace);
				}
			}
			
			
			public override object Visit (NewInitialize newInitializeExpression)
			{
				var result = new ObjectCreateExpression ();
				result.AddChild (new CSharpTokenNode (Convert (newInitializeExpression.Location), "new".Length), ObjectCreateExpression.Roles.Keyword);
				
				if (newInitializeExpression.TypeRequested != null)
					result.AddChild (ConvertToType (newInitializeExpression.TypeRequested), ObjectCreateExpression.Roles.Type);
				
				var location = LocationsBag.GetLocations (newInitializeExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ObjectCreateExpression.Roles.LPar);
				AddArguments (result, location, newInitializeExpression.Arguments);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), ObjectCreateExpression.Roles.RPar);
				
				var init = ConvertCollectionOrObjectInitializers (newInitializeExpression.Initializers);
				if (init != null)
					result.AddChild (init, ObjectCreateExpression.InitializerRole);
				
				return result;
			}
			
			
			public override object Visit (ArrayCreation arrayCreationExpression)
			{
				var result = new ArrayCreateExpression ();
				
				var location = LocationsBag.GetLocations (arrayCreationExpression);
				result.AddChild (new CSharpTokenNode (Convert (arrayCreationExpression.Location), "new".Length), ArrayCreateExpression.Roles.Keyword);
				if (arrayCreationExpression.NewType != null)
					result.AddChild (ConvertToType (arrayCreationExpression.NewType), ArrayCreateExpression.Roles.Type);
					
				var next = arrayCreationExpression.Rank;
				if (arrayCreationExpression.Arguments != null) {
					// skip first array rank.
					next = next.Next;
					
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), ArrayCreateExpression.Roles.LBracket);
					
					var commaLocations = LocationsBag.GetLocations (arrayCreationExpression.Arguments);
					for (int i = 0 ;i < arrayCreationExpression.Arguments.Count; i++) {
						result.AddChild ((Expression)arrayCreationExpression.Arguments[i].Accept (this), ArrayCreateExpression.Roles.Argument);
						if (commaLocations != null && i < commaLocations.Count)
							result.AddChild (new CSharpTokenNode (Convert (commaLocations [i]), 1), ArrayCreateExpression.Roles.Comma);
					}
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), ArrayCreateExpression.Roles.RBracket);
					
				}
				
				while (next != null) {
					ArraySpecifier spec = new ArraySpecifier (next.Dimension);
					var loc = LocationsBag.GetLocations (next);
					spec.AddChild (new CSharpTokenNode (Convert (next.Location), 1), ArraySpecifier.Roles.LBracket);
					result.AddChild (spec, ArrayCreateExpression.AdditionalArraySpecifierRole);
					if (loc != null)
						result.AddChild (new CSharpTokenNode (Convert (loc[0]), 1), ArraySpecifier.Roles.RBracket);
					next = next.Next;
				}
				
				if (arrayCreationExpression.Initializers != null && arrayCreationExpression.Initializers.Count != 0) {
					var initLocation = LocationsBag.GetLocations (arrayCreationExpression.Initializers);
					ArrayInitializerExpression initializer = new ArrayInitializerExpression();
					
					initializer.AddChild (new CSharpTokenNode (Convert (arrayCreationExpression.Initializers.Location), 1), ArrayCreateExpression.Roles.LBrace);
					var commaLocations = LocationsBag.GetLocations (arrayCreationExpression.Initializers.Elements);
					for (int i = 0; i < arrayCreationExpression.Initializers.Count; i++) {
						initializer.AddChild ((Expression)arrayCreationExpression.Initializers[i].Accept (this), ArrayInitializerExpression.Roles.Expression);
						if (commaLocations != null && i < commaLocations.Count) {
							initializer.AddChild (new CSharpTokenNode (Convert (commaLocations [i]), 1), IndexerExpression.Roles.Comma);
						}
					}
					
					if (initLocation != null)
						initializer.AddChild (new CSharpTokenNode (Convert (initLocation[initLocation.Count - 1]), 1), ArrayCreateExpression.Roles.RBrace);
					result.AddChild (initializer, ArrayCreateExpression.InitializerRole);
				}
				
				return result;
			}
			
			public override object Visit (This thisExpression)
			{
				var result = new ThisReferenceExpression ();
				result.Location = Convert (thisExpression.Location);
				return result;
			}
			
			public override object Visit (ArglistAccess argListAccessExpression)
			{
				var result = new UndocumentedExpression () {
					UndocumentedExpressionType = UndocumentedExpressionType.ArgListAccess
				};
				result.AddChild (new CSharpTokenNode (Convert (argListAccessExpression.Location), "__arglist".Length), UndocumentedExpression.Roles.Keyword);
				return result;
			}
			
			#region Undocumented expressions
			public override object Visit (Arglist argListExpression)
			{
				var result = new UndocumentedExpression () { UndocumentedExpressionType = UndocumentedExpressionType.ArgListAccess };
				result.AddChild (new CSharpTokenNode (Convert (argListExpression.Location), "__arglist".Length), UndocumentedExpression.Roles.Keyword);
				var location = LocationsBag.GetLocations (argListExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), UndocumentedExpression.Roles.LPar);
				
				AddArguments (result, location, argListExpression.Arguments);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), UndocumentedExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (MakeRefExpr makeRefExpr)
			{
				var result = new UndocumentedExpression () { UndocumentedExpressionType = UndocumentedExpressionType.RefValue };
				result.AddChild (new CSharpTokenNode (Convert (makeRefExpr.Location), "__makeref".Length), UndocumentedExpression.Roles.Keyword);
				var location = LocationsBag.GetLocations (makeRefExpr);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), UndocumentedExpression.Roles.LPar);
				if (makeRefExpr.Expr != null)
					result.AddChild ((Expression)makeRefExpr.Expr.Accept (this), UndocumentedExpression.Roles.Argument);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), UndocumentedExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (RefTypeExpr refTypeExpr)
			{
				var result = new UndocumentedExpression () { UndocumentedExpressionType = UndocumentedExpressionType.RefValue };
				result.AddChild (new CSharpTokenNode (Convert (refTypeExpr.Location), "__reftype".Length), UndocumentedExpression.Roles.Keyword);
				var location = LocationsBag.GetLocations (refTypeExpr);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), UndocumentedExpression.Roles.LPar);
				
				if (refTypeExpr.Expr != null)
					result.AddChild ((Expression)refTypeExpr.Expr.Accept (this), UndocumentedExpression.Roles.Argument);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), UndocumentedExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (RefValueExpr refValueExpr)
			{
				var result = new UndocumentedExpression () { UndocumentedExpressionType = UndocumentedExpressionType.RefValue };
				result.AddChild (new CSharpTokenNode (Convert (refValueExpr.Location), "__refvalue".Length), UndocumentedExpression.Roles.Keyword);
				var location = LocationsBag.GetLocations (refValueExpr);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), UndocumentedExpression.Roles.LPar);
				
				
				if (refValueExpr.Expr != null)
					result.AddChild ((Expression)refValueExpr.Expr.Accept (this), UndocumentedExpression.Roles.Argument);

				if (refValueExpr.FullNamedExpression != null)
					result.AddChild ((Expression)refValueExpr.FullNamedExpression.Accept (this), UndocumentedExpression.Roles.Argument);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), UndocumentedExpression.Roles.RPar);
				return result;
			}
			#endregion
			
			public override object Visit (TypeOf typeOfExpression)
			{
				var result = new TypeOfExpression ();
				var location = LocationsBag.GetLocations (typeOfExpression);
				result.AddChild (new CSharpTokenNode (Convert (typeOfExpression.Location), "typeof".Length), TypeOfExpression.Roles.Keyword);
				result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), TypeOfExpression.Roles.LPar);
				result.AddChild (ConvertToType (typeOfExpression.TypeExpression), TypeOfExpression.Roles.Type);
				result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), TypeOfExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (SizeOf sizeOfExpression)
			{
				var result = new SizeOfExpression ();
				var location = LocationsBag.GetLocations (sizeOfExpression);
				result.AddChild (new CSharpTokenNode (Convert (sizeOfExpression.Location), "sizeof".Length), TypeOfExpression.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), TypeOfExpression.Roles.LPar);
				result.AddChild (ConvertToType (sizeOfExpression.QueriedType), TypeOfExpression.Roles.Type);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), TypeOfExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (CheckedExpr checkedExpression)
			{
				var result = new CheckedExpression ();
				var location = LocationsBag.GetLocations (checkedExpression);
				result.AddChild (new CSharpTokenNode (Convert (checkedExpression.Location), "checked".Length), TypeOfExpression.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), TypeOfExpression.Roles.LPar);
				result.AddChild ((Expression)checkedExpression.Expr.Accept (this), TypeOfExpression.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), TypeOfExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (UnCheckedExpr uncheckedExpression)
			{
				var result = new UncheckedExpression ();
				var location = LocationsBag.GetLocations (uncheckedExpression);
				result.AddChild (new CSharpTokenNode (Convert (uncheckedExpression.Location), "unchecked".Length), TypeOfExpression.Roles.Keyword);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), TypeOfExpression.Roles.LPar);
				result.AddChild ((Expression)uncheckedExpression.Expr.Accept (this), TypeOfExpression.Roles.Expression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), TypeOfExpression.Roles.RPar);
				return result;
			}
			
			public override object Visit (ElementAccess elementAccessExpression)
			{
				IndexerExpression result = new IndexerExpression ();
				var location = LocationsBag.GetLocations (elementAccessExpression);
				
				result.AddChild ((Expression)elementAccessExpression.Expr.Accept (this), IndexerExpression.Roles.TargetExpression);
				result.AddChild (new CSharpTokenNode (Convert (elementAccessExpression.Location), 1), TypeOfExpression.Roles.LBracket);
				AddArguments (result, location, elementAccessExpression.Arguments);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), TypeOfExpression.Roles.RBracket);
				return result;
			}
			
			public override object Visit (BaseThis baseAccessExpression)
			{
				var result = new BaseReferenceExpression ();
				result.Location = Convert (baseAccessExpression.Location);
				return result;
			}
			
			public override object Visit (StackAlloc stackAllocExpression)
			{
				var result = new StackAllocExpression ();
				
				var location = LocationsBag.GetLocations (stackAllocExpression);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "stackalloc".Length), StackAllocExpression.Roles.Keyword);
				result.AddChild (ConvertToType (stackAllocExpression.TypeExpression), StackAllocExpression.Roles.Type);
				if (location != null && location.Count > 1)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), StackAllocExpression.Roles.LBracket);
				result.AddChild ((Expression)stackAllocExpression.CountExpression.Accept (this), StackAllocExpression.Roles.Expression);
				if (location != null && location.Count > 2)
					result.AddChild (new CSharpTokenNode (Convert (location[2]), 1), StackAllocExpression.Roles.RBracket);
				return result;
			}
			
			public override object Visit (SimpleAssign simpleAssign)
			{
				var result = new AssignmentExpression ();
				
				result.Operator = AssignmentOperatorType.Assign;
				if (simpleAssign.Target != null)
					result.AddChild ((Expression)simpleAssign.Target.Accept (this), AssignmentExpression.LeftRole);
				result.AddChild (new CSharpTokenNode (Convert (simpleAssign.Location), 1), AssignmentExpression.OperatorRole);
				if (simpleAssign.Source != null) {
					result.AddChild ((Expression)simpleAssign.Source.Accept (this), AssignmentExpression.RightRole);
				}
				return result;
			}
			
			public override object Visit (CompoundAssign compoundAssign)
			{
				var result = new AssignmentExpression ();
				int opLength = 2;
				switch (compoundAssign.Op) {
					case Binary.Operator.Multiply:
						result.Operator = AssignmentOperatorType.Multiply;
						break;
					case Binary.Operator.Division:
						result.Operator = AssignmentOperatorType.Divide;
						break;
					case Binary.Operator.Modulus:
						result.Operator = AssignmentOperatorType.Modulus;
						break;
					case Binary.Operator.Addition:
						result.Operator = AssignmentOperatorType.Add;
						break;
					case Binary.Operator.Subtraction:
						result.Operator = AssignmentOperatorType.Subtract;
						break;
					case Binary.Operator.LeftShift:
						result.Operator = AssignmentOperatorType.ShiftLeft;
						opLength = 3;
						break;
					case Binary.Operator.RightShift:
						result.Operator = AssignmentOperatorType.ShiftRight;
						opLength = 3;
						break;
					case Binary.Operator.BitwiseAnd:
						result.Operator = AssignmentOperatorType.BitwiseAnd;
						break;
					case Binary.Operator.BitwiseOr:
						result.Operator = AssignmentOperatorType.BitwiseOr;
						break;
					case Binary.Operator.ExclusiveOr:
						result.Operator = AssignmentOperatorType.ExclusiveOr;
						break;
				}
				
				result.AddChild ((Expression)compoundAssign.Target.Accept (this), AssignmentExpression.LeftRole);
				result.AddChild (new CSharpTokenNode (Convert (compoundAssign.Location), opLength), AssignmentExpression.OperatorRole);
				result.AddChild ((Expression)compoundAssign.Source.Accept (this), AssignmentExpression.RightRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.AnonymousMethodExpression anonymousMethodExpression)
			{
				var result = new AnonymousMethodExpression ();
				var location = LocationsBag.GetLocations (anonymousMethodExpression);
				if (location != null) {
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "delegate".Length), AnonymousMethodExpression.Roles.Keyword);
					
					if (location.Count > 1) {
						result.HasParameterList = true;
						result.AddChild (new CSharpTokenNode (Convert (location[1]), 1), AnonymousMethodExpression.Roles.LPar);
						AddParameter (result, anonymousMethodExpression.Parameters);
						result.AddChild (new CSharpTokenNode (Convert (location[2]), 1), AnonymousMethodExpression.Roles.RPar);
					}
				}
				result.AddChild ((BlockStatement)anonymousMethodExpression.Block.Accept (this), AnonymousMethodExpression.Roles.Body);
				return result;
			}

			public override object Visit (Mono.CSharp.LambdaExpression lambdaExpression)
			{
				var result = new LambdaExpression ();
				var location = LocationsBag.GetLocations (lambdaExpression);
				
				if (location == null || location.Count == 1) {
					AddParameter (result, lambdaExpression.Parameters);
					if (location != null)
						result.AddChild (new CSharpTokenNode (Convert (location [0]), "=>".Length), LambdaExpression.ArrowRole);
				} else {
					result.AddChild (new CSharpTokenNode (Convert (lambdaExpression.Location), 1), LambdaExpression.Roles.LPar);
					AddParameter (result, lambdaExpression.Parameters);
					if (location != null) {
						result.AddChild (new CSharpTokenNode (Convert (location [0]), 1), LambdaExpression.Roles.RPar);
						result.AddChild (new CSharpTokenNode (Convert (location [1]), "=>".Length), LambdaExpression.ArrowRole);
					}
				}
				if (lambdaExpression.Block.IsCompilerGenerated) {
					ContextualReturn generatedReturn = (ContextualReturn)lambdaExpression.Block.Statements [0];
					result.AddChild ((AstNode)generatedReturn.Expr.Accept (this), LambdaExpression.BodyRole);
				} else {
					result.AddChild ((AstNode)lambdaExpression.Block.Accept (this), LambdaExpression.BodyRole);
				}
				
				return result;
			}
			
			public override object Visit (ConstInitializer constInitializer)
			{
				return constInitializer.Expr.Accept (this);
			}
			
			public override object Visit (ArrayInitializer arrayInitializer)
			{
				var result = new ArrayInitializerExpression ();
				var location = LocationsBag.GetLocations (arrayInitializer);
				result.AddChild (new CSharpTokenNode (Convert (arrayInitializer.Location), "{".Length), ArrayInitializerExpression.Roles.LBrace);
				var commaLocations = LocationsBag.GetLocations (arrayInitializer.Elements);
				for (int i = 0; i < arrayInitializer.Count; i++) {
					result.AddChild ((Expression)arrayInitializer[i].Accept (this), ArrayInitializerExpression.Roles.Expression);
					if (commaLocations != null && i < commaLocations.Count)
						result.AddChild (new CSharpTokenNode (Convert (commaLocations[i]), ",".Length), ArrayInitializerExpression.Roles.Comma);
				}
				
				if (location != null) {
					if (location.Count == 2) // optional comma
						result.AddChild (new CSharpTokenNode (Convert (location[1]), ",".Length), ArrayInitializerExpression.Roles.Comma);
					result.AddChild (new CSharpTokenNode (Convert (location[location.Count - 1]), "}".Length), ArrayInitializerExpression.Roles.RBrace);
				}
				return result;
			}
			
			#endregion
			
			#region LINQ expressions
			public override object Visit (Mono.CSharp.Linq.QueryExpression queryExpression)
			{
				var result = new QueryExpression ();
				
				var currentClause = queryExpression.next;
				
				while (currentClause != null) {
					QueryClause clause = (QueryClause)currentClause.Accept (this);
					if (clause is QueryContinuationClause) {
						// insert preceding query at beginning of QueryContinuationClause
						clause.InsertChildAfter(null, result, QueryContinuationClause.PrecedingQueryRole);
						// create a new QueryExpression for the remaining query
						result = new QueryExpression();
					}
					result.AddChild (clause, QueryExpression.ClauseRole);
					currentClause = currentClause.next;
				}
				
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.QueryStartClause queryStart)
			{
				if (queryStart.Expr == null) {
					var intoClause = new QueryContinuationClause ();
					intoClause.AddChild (new CSharpTokenNode (Convert (queryStart.Location), "into".Length), QueryContinuationClause.IntoKeywordRole);
					intoClause.AddChild (Identifier.Create (queryStart.IntoVariable.Name, Convert(queryStart.IntoVariable.Location)), QueryContinuationClause.Roles.Identifier);
					return intoClause;
				}
				
				var fromClause = new QueryFromClause ();
				var location = LocationsBag.GetLocations (queryStart);
				
				fromClause.AddChild (new CSharpTokenNode (Convert (queryStart.Location), "from".Length), QueryFromClause.FromKeywordRole);
				
				if (queryStart.IdentifierType != null)
					fromClause.AddChild (ConvertToType (queryStart.IdentifierType), QueryFromClause.Roles.Type);
				
				fromClause.AddChild (Identifier.Create (queryStart.IntoVariable.Name, Convert(queryStart.IntoVariable.Location)), QueryFromClause.Roles.Identifier);
				
				if (location != null)
					fromClause.AddChild (new CSharpTokenNode (Convert (location[0]), "in".Length), QueryFromClause.InKeywordRole);
				fromClause.AddChild ((Expression)queryStart.Expr.Accept (this), QueryFromClause.Roles.Expression);
				return fromClause;
			}
			
			public override object Visit (Mono.CSharp.Linq.SelectMany queryStart)
			{
				var fromClause = new QueryFromClause ();
				var location = LocationsBag.GetLocations (queryStart);
				
				fromClause.AddChild (new CSharpTokenNode (Convert (queryStart.Location), "from".Length), QueryFromClause.FromKeywordRole);
				
				if (queryStart.IdentifierType != null)
					fromClause.AddChild (ConvertToType (queryStart.IdentifierType), QueryFromClause.Roles.Type);
				
				fromClause.AddChild (Identifier.Create (queryStart.IntoVariable.Name, Convert(queryStart.IntoVariable.Location)), QueryFromClause.Roles.Identifier);
				
				if (location != null)
					fromClause.AddChild (new CSharpTokenNode (Convert (location[0]), "in".Length), QueryFromClause.InKeywordRole);
				fromClause.AddChild ((Expression)queryStart.Expr.Accept (this), QueryFromClause.Roles.Expression);
				return fromClause;
			}
			
			public override object Visit (Mono.CSharp.Linq.Select sel)
			{
				var result = new QuerySelectClause ();
				result.AddChild (new CSharpTokenNode (Convert (sel.Location), "select".Length), QueryWhereClause.Roles.Keyword);
				result.AddChild ((Expression)sel.Expr.Accept (this), QueryWhereClause.Roles.Expression);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.GroupBy groupBy)
			{
				var result = new QueryGroupClause ();
				var location = LocationsBag.GetLocations (groupBy);
				result.AddChild (new CSharpTokenNode (Convert (groupBy.Location), "group".Length), QueryGroupClause.GroupKeywordRole);
				result.AddChild ((Expression)groupBy.ElementSelector.Accept (this), QueryGroupClause.ProjectionRole);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "by".Length), QueryGroupClause.ByKeywordRole);
				result.AddChild ((Expression)groupBy.Expr.Accept (this), QueryGroupClause.KeyRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.Let l)
			{
				var result = new QueryLetClause ();
				var location = LocationsBag.GetLocations (l);
				
				result.AddChild (new CSharpTokenNode (Convert (l.Location), "let".Length), QueryLetClause.Roles.Keyword);
				result.AddChild (Identifier.Create (l.IntoVariable.Name, Convert (l.IntoVariable.Location)), Identifier.Roles.Identifier);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), 1), QueryLetClause.Roles.Assign);
				result.AddChild ((Expression)l.Expr.Accept (this), QueryLetClause.Roles.Expression);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.Where w)
			{
				var result = new QueryWhereClause ();
				var location = LocationsBag.GetLocations (w);
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "where".Length), QueryWhereClause.Roles.Keyword);
				result.AddChild ((Expression)w.Expr.Accept (this), QueryWhereClause.Roles.Condition);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.Join join)
			{
				var result = new QueryJoinClause ();
				var location = LocationsBag.GetLocations (join);
				result.AddChild (new CSharpTokenNode (Convert (join.Location), "join".Length), QueryJoinClause.JoinKeywordRole);
				result.AddChild (Identifier.Create (join.JoinVariable.Name, Convert (join.JoinVariable.Location)), QueryJoinClause.JoinIdentifierRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "in".Length), QueryJoinClause.InKeywordRole);
				
				result.AddChild ((Expression)join.Expr.Accept (this), QueryJoinClause.InExpressionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), "on".Length), QueryJoinClause.OnKeywordRole);
				
				var outer = join.OuterSelector.Statements.FirstOrDefault () as ContextualReturn;
				if (outer != null)
					result.AddChild ((Expression)outer.Expr.Accept (this), QueryJoinClause.OnExpressionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[2]), "equals".Length), QueryJoinClause.EqualsKeywordRole);
				
				var inner = join.InnerSelector.Statements.FirstOrDefault () as ContextualReturn;
				if (inner != null)
					result.AddChild ((Expression)inner.Expr.Accept (this), QueryJoinClause.EqualsExpressionRole);
				
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.GroupJoin join)
			{
				var result = new QueryJoinClause ();
				var location = LocationsBag.GetLocations (join);
				result.AddChild (new CSharpTokenNode (Convert (join.Location), "join".Length), QueryJoinClause.JoinKeywordRole);
				
				// mcs seems to have swapped IntoVariable with JoinVariable, so we'll swap it back here
				result.AddChild (Identifier.Create (join.IntoVariable.Name, Convert (join.IntoVariable.Location)), QueryJoinClause.JoinIdentifierRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[0]), "in".Length), QueryJoinClause.InKeywordRole);
				
				var outer = join.OuterSelector.Statements.FirstOrDefault () as ContextualReturn;
				if (outer != null)
					result.AddChild ((Expression)outer.Expr.Accept (this), QueryJoinClause.OnExpressionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[1]), "on".Length), QueryJoinClause.OnKeywordRole);
				result.AddChild ((Expression)join.Expr.Accept (this), QueryJoinClause.InExpressionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[2]), "equals".Length), QueryJoinClause.EqualsKeywordRole);
				var inner = join.InnerSelector.Statements.FirstOrDefault () as ContextualReturn;
				if (inner != null)
					result.AddChild ((Expression)inner.Expr.Accept (this), QueryJoinClause.EqualsExpressionRole);
				
				if (location != null)
					result.AddChild (new CSharpTokenNode (Convert (location[3]), "into".Length), QueryJoinClause.IntoKeywordRole);
				
				result.AddChild (Identifier.Create (join.JoinVariable.Name, Convert (join.JoinVariable.Location)), QueryJoinClause.IntoIdentifierRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.OrderByAscending orderByAscending)
			{
				var result = new QueryOrderClause ();
				
				var ordering = new QueryOrdering ();
				
				ordering.AddChild ((Expression)orderByAscending.Expr.Accept (this), QueryWhereClause.Roles.Expression);
				var location = LocationsBag.GetLocations (orderByAscending);
				if (location != null) {
					ordering.Direction = QueryOrderingDirection.Ascending;
					ordering.AddChild (new CSharpTokenNode (Convert (location[0]), "ascending".Length), QueryWhereClause.Roles.Keyword);
				}
				result.AddChild (ordering, QueryOrderClause.OrderingRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.OrderByDescending orderByDescending)
			{
				var result = new QueryOrderClause ();
				
				var ordering = new QueryOrdering ();
				
				ordering.AddChild ((Expression)orderByDescending.Expr.Accept (this), QueryWhereClause.Roles.Expression);
				var location = LocationsBag.GetLocations (orderByDescending);
				if (location != null) {
					ordering.Direction = QueryOrderingDirection.Descending;
					ordering.AddChild (new CSharpTokenNode (Convert (location[0]), "ascending".Length), QueryWhereClause.Roles.Keyword);
				}
				result.AddChild (ordering, QueryOrderClause.OrderingRole);
				return result;
		}
			
			public override object Visit (Mono.CSharp.Linq.ThenByAscending thenByAscending)
			{
				var result = new QueryOrderClause ();
				
				var ordering = new QueryOrdering ();
				
				ordering.AddChild ((Expression)thenByAscending.Expr.Accept (this), QueryWhereClause.Roles.Expression);
				var location = LocationsBag.GetLocations (thenByAscending);
				if (location != null) {
					ordering.Direction = QueryOrderingDirection.Ascending;
					ordering.AddChild (new CSharpTokenNode (Convert (location[0]), "ascending".Length), QueryWhereClause.Roles.Keyword);
				}
				result.AddChild (ordering, QueryOrderClause.OrderingRole);
				return result;
			}
			
			public override object Visit (Mono.CSharp.Linq.ThenByDescending thenByDescending)
			{
				var result = new QueryOrderClause ();
				
				var ordering = new QueryOrdering ();
				
				ordering.AddChild ((Expression)thenByDescending.Expr.Accept (this), QueryWhereClause.Roles.Expression);
				var location = LocationsBag.GetLocations (thenByDescending);
				if (location != null) {
					ordering.Direction = QueryOrderingDirection.Descending;
					ordering.AddChild (new CSharpTokenNode (Convert (location[0]), "ascending".Length), QueryWhereClause.Roles.Keyword);
				}
				result.AddChild (ordering, QueryOrderClause.OrderingRole);
				return result;
			}
			#endregion
		}
		
		public CSharpParser ()
		{
			CompilerArguments = new string[] { "-v", "-unsafe"};
		}
		
		public CSharpParser (string[] args)
		{
			CompilerArguments = args;
		}
		
		static AstNode GetOuterLeft (AstNode node)
		{
			var outerLeft = node;
			while (outerLeft.FirstChild != null)
				outerLeft = outerLeft.FirstChild;
			return outerLeft;
		}
		
		static AstNode NextLeaf (AstNode node)
		{
			if (node == null)
				return null;
			var next = node.NextSibling;
			if (next == null)
				return node.Parent;
			return GetOuterLeft(next);
		}
		
		static void InsertComments (CompilerCompilationUnit top, ConversionVisitor conversionVisitor)
		{
			var leaf = GetOuterLeft (conversionVisitor.Unit);
			
			for (int i = 0; i < top.SpecialsBag.Specials.Count; i++) {
				var special = top.SpecialsBag.Specials [i];
				Comment newLeaf = null;
				
				var comment = special as SpecialsBag.Comment;
				if (comment != null) {
					if (conversionVisitor.convertTypeSystemMode && (comment.CommentType != SpecialsBag.CommentType.Documentation))
						continue;
					var type = (CommentType)comment.CommentType;
					var start = new TextLocation (comment.Line, comment.Col);
					var end = new TextLocation (comment.EndLine, comment.EndCol);
					newLeaf = new Comment (type, start, end) {
							StartsLine = comment.StartsLine,
							Content = comment.Content
					};
				} else {
					// TODO: Proper handling of pre processor directives (atm got treated as comments Ast wise)
					var directive = special as SpecialsBag.PreProcessorDirective;
					if (directive != null) {
						newLeaf = new Comment (CommentType.SingleLine, new TextLocation (directive.Line, directive.Col), new TextLocation (directive.EndLine, directive.EndCol + 1));
						
						if (!directive.Take) {
							SpecialsBag.PreProcessorDirective endif = null;
							int endifLevel = 0;
							for (int j = i + 1; j < top.SpecialsBag.Specials.Count; j++) {
								var s = top.SpecialsBag.Specials [j] as SpecialsBag.PreProcessorDirective;
								if (s == null)
									continue;
								if (s.Cmd == Tokenizer.PreprocessorDirective.If) {
									endifLevel++;
									continue;
								}
								if (s.Cmd == Tokenizer.PreprocessorDirective.Endif || endifLevel == 0 && s.Cmd == Tokenizer.PreprocessorDirective.Else) {
									if (endifLevel == 0) {
										endif = s;
										i = j;
										break;
									}
									endifLevel--;
								}
							}
							if (endif != null)
								newLeaf = new Comment (CommentType.SingleLine, new TextLocation (directive.Line, directive.Col), new TextLocation (endif.EndLine, endif.EndCol));
						}
					}
				}
				
				if (newLeaf == null)
					continue;
				
				while (true) {
					var nextLeaf = NextLeaf (leaf);
					// insert comment at begin
					if (newLeaf.StartLocation < leaf.StartLocation) {
						var node = leaf.Parent ?? conversionVisitor.Unit;
						while (node.Parent != null && node.FirstChild == leaf) {
							leaf = node;
							node = node.Parent;
						}
						node.InsertChildBefore (leaf, newLeaf, AstNode.Roles.Comment);
						leaf = newLeaf;
						break;
					}
					
					// insert comment at the end
					if (nextLeaf == null) {
						var node = leaf.Parent ?? conversionVisitor.Unit;
						node.AddChild (newLeaf, AstNode.Roles.Comment);
						leaf = newLeaf;
						break;
					}
					
					// comment is between 2 nodes
					if (leaf.EndLocation <= newLeaf.StartLocation && newLeaf.StartLocation <= nextLeaf.StartLocation) {
						var node = leaf.Parent ?? conversionVisitor.Unit;
						node.InsertChildAfter (leaf, newLeaf, AstNode.Roles.Comment);
						leaf = newLeaf;
						break;
					}
					leaf = nextLeaf;
				}
			}
		}
		
		internal static CompilationUnit Parse (bool convertTypeSystemMode, CompilerCompilationUnit top)
		{
			if (top == null)
				return null;
			CSharpParser.ConversionVisitor conversionVisitor = new ConversionVisitor (convertTypeSystemMode, top.LocationsBag);
			top.UsingsBag.Global.Accept (conversionVisitor);
			conversionVisitor.AddAttributeSection (conversionVisitor.Unit, top.ModuleCompiled);
			InsertComments (top, conversionVisitor);
			return conversionVisitor.Unit;
		}
		
		public class ErrorReportPrinter : ReportPrinter
		{
			readonly string fileName;
			public readonly List<Error> Errors = new List<Error> ();
			
			public ErrorReportPrinter (string fileName)
			{
				this.fileName = fileName;
			}
			
			public override void Print (AbstractMessage msg)
			{
				base.Print (msg);
				var newError = new Error (msg.IsWarning ? ErrorType.Warning : ErrorType.Error, msg.Text, new DomRegion (fileName, msg.Location.Row, msg.Location.Column));
				Errors.Add (newError);
			}
		}
		ErrorReportPrinter errorReportPrinter = new ErrorReportPrinter (null);
		
		public ErrorReportPrinter ErrorPrinter {
			get {
				return errorReportPrinter;
			}
		}
		
		public bool HasErrors {
			get {
				return errorReportPrinter.ErrorsCount + errorReportPrinter.FatalCounter > 0;
			}
		}
		
		public bool HasWarnings {
			get {
				return errorReportPrinter.WarningsCount > 0;
			}
		}
		
		public CompilationUnit Parse (TextReader reader, int line = 0)
		{
			// TODO: can we optimize this to avoid the text->stream->text roundtrip?
			using (MemoryStream stream = new MemoryStream ()) {
				StreamWriter w = new StreamWriter (stream, Encoding.UTF8);
				char[] buffer = new char[2048];
				int read;
				while ((read = reader.ReadBlock(buffer, 0, buffer.Length)) > 0)
					w.Write (buffer, 0, read);
				w.Flush (); // we can't close the StreamWriter because that would also close the MemoryStream
				stream.Position = 0;
				return Parse (stream, line);
			}
		}

		public static void AdjustLineLocations (AstNode node, int line)
		{
			if (node is IRelocatable) {
				((IRelocatable)node).SetStartLocation (new TextLocation (node.StartLocation.Line + line, node.StartLocation.Column));
			}
			foreach (var child in node.Children) {
				AdjustLineLocations (child, line);
			}
		}
		
		public CompilationUnit Parse (CompilerCompilationUnit top, int line)
		{
			if (top == null)
				return null;
			CSharpParser.ConversionVisitor conversionVisitor = new ConversionVisitor (GenerateTypeSystemMode, top.LocationsBag);
			top.UsingsBag.Global.Accept (conversionVisitor);
			conversionVisitor.AddAttributeSection (conversionVisitor.Unit, top.ModuleCompiled);
			InsertComments (top, conversionVisitor);
			if (CompilationUnitCallback != null)
				CompilationUnitCallback (top);
			if (line != 0)
				AdjustLineLocations (conversionVisitor.Unit, line);
			if (top.LastYYValue is Mono.CSharp.Expression)
				conversionVisitor.Unit.TopExpression = ((Mono.CSharp.Expression)top.LastYYValue).Accept (conversionVisitor) as AstNode;
			return conversionVisitor.Unit;
		}
		
		public string[] CompilerArguments {
			get;
			private set;
		}
		
		public Action<CompilerCompilationUnit> CompilationUnitCallback {
			get;
			set;
		}
		
		public bool GenerateTypeSystemMode {
			get;
			set;
		}
		
		public CompilationUnit Parse (Stream stream, int line = 0)
		{
			lock (CompilerCallableEntryPoint.parseLock) {
				errorReportPrinter = new ErrorReportPrinter ("");
				CompilerCompilationUnit top = CompilerCallableEntryPoint.ParseFile (CompilerArguments, stream, "parsed.cs", errorReportPrinter);
				var unit = Parse (top, line);
				unit.Errors.AddRange (errorReportPrinter.Errors);
				return unit;
			}
		}

		public IEnumerable<AttributedNode> ParseTypeMembers (TextReader reader, int lineModifier = 0)
		{
			string code = "unsafe partial class MyClass { " + Environment.NewLine + reader.ReadToEnd () + "}";
			var cu = Parse (new StringReader (code), -1 + lineModifier);
			if (cu == null)
				return Enumerable.Empty<AttributedNode> ();
			var td = cu.Children.FirstOrDefault () as TypeDeclaration;
			if (td != null)
				return td.Members;
			return Enumerable.Empty<AttributedNode> ();
		}
		
		public IEnumerable<Statement> ParseStatements(TextReader reader, int lineModifier = 0)
		{
			string code = "void M() { " + Environment.NewLine + reader.ReadToEnd() + "}";
			var members = ParseTypeMembers(new StringReader(code), lineModifier - 1);
			var method = members.FirstOrDefault() as MethodDeclaration;
			if (method != null && method.Body != null)
				return method.Body.Statements;
			return Enumerable.Empty<Statement> ();
		}
		
		public AstType ParseTypeReference(TextReader reader)
		{
			string code = reader.ReadToEnd() + " a;";
			var members = ParseTypeMembers(new StringReader(code));
			var field = members.FirstOrDefault() as FieldDeclaration;
			if (field != null)
				return field.ReturnType;
			return AstType.Null;
		}
		
		public AstNode ParseExpression(TextReader reader)
		{
			var es = ParseStatements(new StringReader("tmp = " + Environment.NewLine + reader.ReadToEnd() + ";"), -1).FirstOrDefault() as ExpressionStatement;
			if (es != null) {
				AssignmentExpression ae = es.Expression as AssignmentExpression;
				if (ae != null)
					return ae.Right;
			}
			return null;
		}
		
		/// <summary>
		/// Parses a file snippet; guessing what the code snippet represents (compilation unit, type members, block, type reference, expression).
		/// </summary>
		public AstNode ParseSnippet(TextReader reader)
		{
			// TODO: add support for parsing a part of a file
			throw new NotImplementedException();
		}
	}
}
