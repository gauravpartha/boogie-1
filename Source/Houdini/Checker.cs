//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Diagnostics.Contracts;
using System.Collections.Generic;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using Microsoft.Basetypes;
using System.Collections;
using System.IO;
using System.Threading;
using VC;
using System.Linq;

namespace Microsoft.Boogie.Houdini {
  public class ExistentialConstantCollector : StandardVisitor {
      public static void CollectHoudiniConstants(Houdini houdini, Implementation impl, out ExistentialConstantCollector collector)
      {
          collector = new ExistentialConstantCollector(houdini);
          collector.impl = impl;
          collector.VisitImplementation(impl);
      }

    private ExistentialConstantCollector(Houdini houdini) {
      this.houdini = houdini;
      this.houdiniAssertConstants = new HashSet<Variable>();
      this.houdiniAssumeConstants = new HashSet<Variable>();

      this.explainNegative = new HashSet<Variable>();
      this.explainPositive = new HashSet<Variable>();
      this.constToControl = new Dictionary<string, Tuple<Variable, Variable>>();
    }
    private Houdini houdini;
    public HashSet<Variable> houdiniAssertConstants;
    public HashSet<Variable> houdiniAssumeConstants;

    // Explain Houdini stuff
    public HashSet<Variable> explainPositive;
    public HashSet<Variable> explainNegative;
    public Dictionary<string, Tuple<Variable, Variable>> constToControl;
    Implementation impl;

    public override Cmd VisitAssertRequiresCmd(AssertRequiresCmd node) {
      AddHoudiniConstant(node);
      return base.VisitAssertRequiresCmd(node);
    }
    public override Cmd VisitAssertEnsuresCmd(AssertEnsuresCmd node) {
      AddHoudiniConstant(node);
      return base.VisitAssertEnsuresCmd(node);
    }
    public override Cmd VisitAssertCmd(AssertCmd node) {
      AddHoudiniConstant(node);
      return base.VisitAssertCmd(node);
    }
    public override Cmd VisitAssumeCmd(AssumeCmd node) {
      AddHoudiniConstant(node);
      return base.VisitAssumeCmd(node);
    }
    private void AddHoudiniConstant(AssertCmd assertCmd)
    {
        Variable houdiniConstant;
        if (houdini.MatchCandidate(assertCmd.Expr, out houdiniConstant))
            houdiniAssertConstants.Add(houdiniConstant);
        
        if (houdiniConstant != null && CommandLineOptions.Clo.ExplainHoudini)
        {
            var control = createNewExplainConstants(houdiniConstant);
            assertCmd.Expr = houdini.InsertCandidateControl(assertCmd.Expr, control.Item1, control.Item2);
            explainPositive.Add(control.Item1);
            explainNegative.Add(control.Item2);
            constToControl.Add(houdiniConstant.Name, control);
        }
    }
    private void AddHoudiniConstant(AssumeCmd assumeCmd)
    {
        Variable houdiniConstant;
        if (houdini.MatchCandidate(assumeCmd.Expr, out houdiniConstant))
            houdiniAssumeConstants.Add(houdiniConstant);
    }
    private Tuple<Variable, Variable> createNewExplainConstants(Variable v)
    {
        Contract.Assert(impl != null);
        Contract.Assert(CommandLineOptions.Clo.ExplainHoudini);
        Variable v1 = new Constant(Token.NoToken, new TypedIdent(Token.NoToken, string.Format("{0}_{1}_{2}", v.Name, impl.Name, "pos"), Microsoft.Boogie.BasicType.Bool));
        Variable v2 = new Constant(Token.NoToken, new TypedIdent(Token.NoToken, string.Format("{0}_{1}_{2}", v.Name, impl.Name, "neg"), Microsoft.Boogie.BasicType.Bool));

        return Tuple.Create(v1, v2);
    }
  }

  public class HoudiniSession {
    public static double proverTime = 0;
    public static int numProverQueries = 0;
    public static double unsatCoreProverTime = 0;
    public static int numUnsatCoreProverQueries = 0;
    public static int numUnsatCorePrunings = 0;

    public string descriptiveName;
    private VCExpr conjecture;
    private ProverInterface.ErrorHandler handler;
    ConditionGeneration.CounterexampleCollector collector;
    HashSet<Variable> unsatCoreSet;
    HashSet<Variable> houdiniConstants;
    public HashSet<Variable> houdiniAssertConstants;
    private HashSet<Variable> houdiniAssumeConstants;
    
    private HashSet<Variable> explainConstantsPositive;
    private HashSet<Variable> explainConstantsNegative;
    private Dictionary<string, Tuple<Variable, Variable>> constantToControl;

    Houdini houdini;
    Implementation implementation;

    public bool InUnsatCore(Variable constant) {
      if (unsatCoreSet == null)
        return true;
      if (unsatCoreSet.Contains(constant))
        return true;
      numUnsatCorePrunings++;
      return false;
    }

    public HoudiniSession(Houdini houdini, VCGen vcgen, ProverInterface proverInterface, Program program, Implementation impl) {
      descriptiveName = impl.Name;
      collector = new ConditionGeneration.CounterexampleCollector();
      collector.OnProgress("HdnVCGen", 0, 0, 0.0);

      vcgen.ConvertCFG2DAG(impl);
      ModelViewInfo mvInfo;
      Hashtable/*TransferCmd->ReturnCmd*/ gotoCmdOrigins = vcgen.PassifyImpl(impl, out mvInfo);

      ExistentialConstantCollector ecollector;
      ExistentialConstantCollector.CollectHoudiniConstants(houdini, impl, out ecollector);
      this.houdiniAssertConstants = ecollector.houdiniAssertConstants;
      this.houdiniAssumeConstants = ecollector.houdiniAssumeConstants;
      this.explainConstantsNegative = ecollector.explainNegative;
      this.explainConstantsPositive = ecollector.explainPositive;
      this.constantToControl = ecollector.constToControl;

      houdiniConstants = new HashSet<Variable>();
      houdiniConstants.UnionWith(houdiniAssertConstants);
      houdiniConstants.UnionWith(houdiniAssumeConstants);

      var exprGen = proverInterface.Context.ExprGen;
      VCExpr controlFlowVariableExpr = CommandLineOptions.Clo.UseLabels ? null : exprGen.Integer(BigNum.ZERO);

      Hashtable/*<int, Absy!>*/ label2absy;
      conjecture = vcgen.GenerateVC(impl, controlFlowVariableExpr, out label2absy, proverInterface.Context);
      if (!CommandLineOptions.Clo.UseLabels) {
        VCExpr controlFlowFunctionAppl = exprGen.ControlFlowFunctionApplication(exprGen.Integer(BigNum.ZERO), exprGen.Integer(BigNum.ZERO));
        VCExpr eqExpr = exprGen.Eq(controlFlowFunctionAppl, exprGen.Integer(BigNum.FromInt(impl.Blocks[0].UniqueId)));
        conjecture = exprGen.Implies(eqExpr, conjecture);
      }

      Macro macro = new Macro(Token.NoToken, descriptiveName, new VariableSeq(), new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Type.Bool), false));
      proverInterface.DefineMacro(macro, conjecture);
      conjecture = exprGen.Function(macro);

      if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Local) {
        handler = new VCGen.ErrorReporterLocal(gotoCmdOrigins, label2absy, impl.Blocks, vcgen.incarnationOriginMap, collector, mvInfo, proverInterface.Context, program);
      }
      else {
        handler = new VCGen.ErrorReporter(gotoCmdOrigins, label2absy, impl.Blocks, vcgen.incarnationOriginMap, collector, mvInfo, proverInterface.Context, program);
      }

      this.houdini = houdini;
      this.implementation = impl;
    }

    private VCExpr BuildAxiom(ProverInterface proverInterface, Dictionary<Variable, bool> currentAssignment) {
      ProverContext proverContext = proverInterface.Context;
      Boogie2VCExprTranslator exprTranslator = proverContext.BoogieExprTranslator;
      VCExpressionGenerator exprGen = proverInterface.VCExprGen;

      VCExpr expr = VCExpressionGenerator.True;

      foreach (KeyValuePair<Variable, bool> kv in currentAssignment) {
        Variable constant = kv.Key;
        VCExprVar exprVar = exprTranslator.LookupVariable(constant);
        if (kv.Value) {
          expr = exprGen.And(expr, exprVar);
        }
        else {
          expr = exprGen.And(expr, exprGen.Not(exprVar));
        }
      }

      if (CommandLineOptions.Clo.ExplainHoudini)
      {
          // default values for control variables
          foreach (var constant in explainConstantsNegative.Concat(explainConstantsPositive))
          {
              expr = exprGen.And(expr, exprTranslator.LookupVariable(constant));
          }
      }

      /*
      foreach (Variable constant in this.houdiniConstants) {
        VCExprVar exprVar = exprTranslator.LookupVariable(constant);
        if (currentAssignment[constant]) {
          expr = exprGen.And(expr, exprVar);
        }
        else {
          expr = exprGen.And(expr, exprGen.Not(exprVar));
        }
      }
       */
      return expr;
    }

    public ProverInterface.Outcome Verify(ProverInterface proverInterface, Dictionary<Variable, bool> assignment, out List<Counterexample> errors) {
      collector.examples.Clear();

      if (CommandLineOptions.Clo.Trace) {
        Console.WriteLine("Verifying " + descriptiveName);
      }
      DateTime now = DateTime.UtcNow;

      VCExpr vc = proverInterface.VCExprGen.Implies(BuildAxiom(proverInterface, assignment), conjecture);
      proverInterface.BeginCheck(descriptiveName, vc, handler);
      ProverInterface.Outcome proverOutcome = proverInterface.CheckOutcome(handler);

      double queryTime = (DateTime.UtcNow - now).TotalSeconds;
      proverTime += queryTime;
      numProverQueries++;
      if (CommandLineOptions.Clo.Trace) {
        Console.WriteLine("Time taken = " + queryTime);
      }

      errors = collector.examples;
      return proverOutcome;
    }

    // MAXSAT
    public void Explain(ProverInterface proverInterface, 
        Dictionary<Variable, bool> assignment, Variable refutedConstant)
    {
        Contract.Assert(CommandLineOptions.Clo.ExplainHoudini);

        collector.examples.Clear();

        // debugging
        houdiniAssertConstants.Iter(v => System.Diagnostics.Debug.Assert(assignment.ContainsKey(v)));
        houdiniAssumeConstants.Iter(v => System.Diagnostics.Debug.Assert(assignment.ContainsKey(v)));
        Contract.Assert(assignment.ContainsKey(refutedConstant));
        Contract.Assert(houdiniAssertConstants.Contains(refutedConstant));

        var hardAssumptions = new List<VCExpr>();
        var softAssumptions = new List<VCExpr>();

        Boogie2VCExprTranslator exprTranslator = proverInterface.Context.BoogieExprTranslator;
        VCExpressionGenerator exprGen = proverInterface.VCExprGen;
        var controlExpr = VCExpressionGenerator.True;

        foreach (var tup in assignment)
        {
            Variable constant = tup.Key;
            VCExprVar exprVar = exprTranslator.LookupVariable(constant);
            var val = tup.Value;

            if (houdiniAssumeConstants.Contains(constant))
            {
                if (tup.Value)
                    hardAssumptions.Add(exprVar);
                else
                    softAssumptions.Add(exprVar);
            }
            else if (houdiniAssertConstants.Contains(constant))
            {
                if (constant == refutedConstant)
                    hardAssumptions.Add(exprVar);
                else
                    hardAssumptions.Add(exprGen.Not(exprVar));
            }
            else
            {
                if (tup.Value)
                    hardAssumptions.Add(exprVar);
                else
                    hardAssumptions.Add(exprGen.Not(exprVar));
            }

            if (constant != refutedConstant && constantToControl.ContainsKey(constant.Name))
            {
                var posControl = constantToControl[constant.Name].Item1;
                var negControl = constantToControl[constant.Name].Item2;

                // Handle self-recursion
                if (houdiniAssertConstants.Contains(constant) && houdiniAssumeConstants.Contains(constant))
                {
                    // disable this assert
                    controlExpr = exprGen.And(controlExpr, exprGen.And(exprTranslator.LookupVariable(posControl), exprGen.Not(exprTranslator.LookupVariable(negControl))));
                }
                else
                {
                    // default values for control variables
                    controlExpr = exprGen.And(controlExpr, exprGen.And(exprTranslator.LookupVariable(posControl), exprTranslator.LookupVariable(negControl)));
                }
            }
        }

        hardAssumptions.Add(exprGen.Not(conjecture));

        // default values for control variables
        Contract.Assert(constantToControl.ContainsKey(refutedConstant.Name));
        var pc = constantToControl[refutedConstant.Name].Item1;
        var nc = constantToControl[refutedConstant.Name].Item2;

        var controlExprNoop = exprGen.And(controlExpr,
            exprGen.And(exprTranslator.LookupVariable(pc), exprTranslator.LookupVariable(nc)));

        var controlExprFalse = exprGen.And(controlExpr,
            exprGen.And(exprGen.Not(exprTranslator.LookupVariable(pc)), exprGen.Not(exprTranslator.LookupVariable(nc))));

        if (CommandLineOptions.Clo.Trace)
        {
            Console.WriteLine("Verifying (MaxSat) " + descriptiveName);
        }
        DateTime now = DateTime.UtcNow;

        var el = CommandLineOptions.Clo.ProverCCLimit;
        CommandLineOptions.Clo.ProverCCLimit = 1;

        List<int> unsatisfiedSoftAssumptions;
        
        hardAssumptions.Add(controlExprNoop);
        proverInterface.CheckAssumptions(hardAssumptions, softAssumptions, out unsatisfiedSoftAssumptions, handler);
        hardAssumptions.RemoveAt(hardAssumptions.Count - 1);

        var reason = new HashSet<string>();
        unsatisfiedSoftAssumptions.Iter(i => reason.Add(softAssumptions[i].ToString()));
        if (CommandLineOptions.Clo.Trace)
        {
            Console.Write("Reason for removal of {0}: ", refutedConstant.Name);
            reason.Iter(r => Console.Write("{0} ", r));
            Console.WriteLine();
        }

        hardAssumptions.Add(controlExprFalse);
        var softAssumptions2 = new List<VCExpr>();
        for (int i = 0; i < softAssumptions.Count; i++)
        {
            if (unsatisfiedSoftAssumptions.Contains(i))
            {
                softAssumptions2.Add(softAssumptions[i]);
                continue;
            }
            hardAssumptions.Add(softAssumptions[i]);
        }

        var unsatisfiedSoftAssumptions2 = new List<int>();
        proverInterface.CheckAssumptions(hardAssumptions, softAssumptions2, out unsatisfiedSoftAssumptions2, handler);

        unsatisfiedSoftAssumptions2.Iter(i => reason.Remove(softAssumptions2[i].ToString()));
        if (CommandLineOptions.Clo.Trace)
        {
            Console.Write("Revised reason for removal of {0}: ", refutedConstant.Name);
            reason.Iter(r => Console.Write("{0} ", r));
            Console.WriteLine();
        }
        foreach (var r in reason)
        {
            Houdini.explainHoudiniDottyFile.WriteLine("{0} -> {1} [ label = \"\" color=red ];", refutedConstant.Name, r);
        }

        CommandLineOptions.Clo.ProverCCLimit = el;

        double queryTime = (DateTime.UtcNow - now).TotalSeconds;
        proverTime += queryTime;
        numProverQueries++;
        if (CommandLineOptions.Clo.Trace)
        {
            Console.WriteLine("Time taken = " + queryTime);
        }
    }

    public void UpdateUnsatCore(ProverInterface proverInterface, Dictionary<Variable, bool> assignment)
    {
      DateTime now = DateTime.UtcNow;

      Boogie2VCExprTranslator exprTranslator = proverInterface.Context.BoogieExprTranslator;
      proverInterface.Push();
      proverInterface.Assert(conjecture, false);
      foreach (var v in assignment.Keys) {
        if (assignment[v]) continue;
        proverInterface.Assert(exprTranslator.LookupVariable(v), false);
      }
      List<Variable> assumptionVars = new List<Variable>();
      List<VCExpr> assumptionExprs = new List<VCExpr>();
      foreach (var v in assignment.Keys) {
        if (!assignment[v]) continue;
        assumptionVars.Add(v);
        assumptionExprs.Add(exprTranslator.LookupVariable(v));
      }
      List<int> unsatCore;
      ProverInterface.Outcome tmp = proverInterface.CheckAssumptions(assumptionExprs, out unsatCore, handler);
      System.Diagnostics.Debug.Assert(tmp == ProverInterface.Outcome.Valid);
      unsatCoreSet = new HashSet<Variable>();
      foreach (int i in unsatCore)
        unsatCoreSet.Add(assumptionVars[i]);
      proverInterface.Pop();

      double unsatCoreQueryTime = (DateTime.UtcNow - now).TotalSeconds;
      unsatCoreProverTime += unsatCoreQueryTime;
      numUnsatCoreProverQueries++;
    }

  }
}