using System.Collections.Generic;

namespace Microsoft.Boogie
{
  class GlobalSnapshotInstrumentation
  {
    private CivlTypeChecker civlTypeChecker;
    private Dictionary<Variable, Variable> oldGlobalMap;
    private List<Variable> newLocalVars;

    public GlobalSnapshotInstrumentation(CivlTypeChecker civlTypeChecker)
    {
      this.civlTypeChecker = civlTypeChecker;
      newLocalVars = new List<Variable>();
      oldGlobalMap = new Dictionary<Variable, Variable>();
      foreach (Variable g in civlTypeChecker.GlobalVariables)
      {
        LocalVariable l = OldGlobalLocal(g);
        oldGlobalMap[g] = l;
        newLocalVars.Add(l);
      }
    }

    public Dictionary<Variable, Variable> OldGlobalMap => oldGlobalMap;

    public List<Variable> NewLocalVars => newLocalVars;

    public List<Cmd> CreateUpdatesToOldGlobalVars()
    {
      List<IdentifierExpr> lhss = new List<IdentifierExpr>();
      List<Expr> rhss = new List<Expr>();
      foreach (Variable g in oldGlobalMap.Keys)
      {
        lhss.Add(Expr.Ident(oldGlobalMap[g]));
        rhss.Add(Expr.Ident(g));
      }

      var cmds = new List<Cmd>();
      if (lhss.Count > 0)
      {
        cmds.Add(CmdHelper.AssignCmd(lhss, rhss));
      }

      return cmds;
    }

    public List<Cmd> CreateInitCmds()
    {
      List<IdentifierExpr> lhss = new List<IdentifierExpr>();
      List<Expr> rhss = new List<Expr>();
      foreach (Variable g in oldGlobalMap.Keys)
      {
        lhss.Add(Expr.Ident(oldGlobalMap[g]));
        rhss.Add(Expr.Ident(g));
      }

      var initCmds = new List<Cmd>();
      if (lhss.Count > 0)
      {
        initCmds.Add(CmdHelper.AssignCmd(lhss, rhss));
      }

      return initCmds;
    }

    private LocalVariable OldGlobalLocal(Variable v)
    {
      return civlTypeChecker.LocalVariable($"global_old_{v.Name}", v.TypedIdent.Type);
    }
  }
}