using DistantWorlds.Types;
using DistantWorlds.UI;

namespace AdvancedConstructionMod;

// Native paged choices do not depend on missing Unicode glyphs or a global designer popup.
internal sealed class SeriesChoiceWindow:NativeWindow
{
    private readonly (string Text,Action Pick)[] choices;
    private readonly Action? returnTo;
    private readonly DistantWorlds.UI.DWButton[] rows;
    private int page;
    internal SeriesChoiceWindow(string title,IEnumerable<(string Text,Action Pick)> choices,Action? returnTo=null,string? explanation=null):base("ACM.Choices",title,720,540)
    {
        this.choices=choices.ToArray();this.returnTo=returnTo;
        float help=explanation==null?0:S(72);
        if(explanation!=null)Label("ACM.ChoiceHelp",explanation,S(12),S(40),Panel.Size.X-S(24),S(64));
        int count=Math.Max(1,(int)((Panel.Size.Y-S(148)-help)/S(44)));
        rows=Enumerable.Range(0,count).Select(i=>Button("ACM.Choice."+i,"",S(12),S(44)+help+i*S(44),Panel.Size.X-S(24),()=>Choose(i),S(40))).ToArray();
        Status=Label("ACM.ChoiceStatus","",S(12),Panel.Size.Y-S(100),Panel.Size.X-S(24),S(24));
        Button("ACM.ChoicePrevious","Previous",S(12),Panel.Size.Y-S(68),S(100),()=>{if(page>0){page--;Bind();}});
        Button("ACM.ChoiceNext","Next",S(124),Panel.Size.Y-S(68),S(100),()=>{if((page+1)*rows.Length<this.choices.Length){page++;Bind();}});
        Button("ACM.ChoiceCancel","Cancel",Panel.Size.X-S(124),Panel.Size.Y-S(68),S(112),Close);Bind();
    }
    private void Bind()
    {
        for(int i=0;i<rows.Length;i++)
        {
            int n=page*rows.Length+i;rows[i].Visible=n<choices.Length;
            if(rows[i].Visible){rows[i].Text=choices[n].Text;rows[i].HoverTips.Clear();rows[i].AddHoverTip("Choose",choices[n].Text,rows[i].CalculateAbsoluteRectangle(),440,Side.Right);}
        }
        foreach(var control in Panel.Controls.OfType<DistantWorlds.UI.DWButton>())
        {if(control.Name=="ACM.ChoicePrevious")control.Enabled=page>0;else if(control.Name=="ACM.ChoiceNext")control.Enabled=(page+1)*rows.Length<choices.Length;}
        Message(choices.Length==0?"No compatible choices. Cancel to keep the current plan.":$"Page {page+1} / {Math.Max(1,(choices.Length+rows.Length-1)/rows.Length)}. Choice returns to the previous screen; Save / Apply commits it.");
    }
    private void Choose(int i){int n=page*rows.Length+i;if(n>=choices.Length||!ReferenceEquals(Active,this))return;Close();choices[n].Pick();}
    internal override void Close(){bool active=ReferenceEquals(Active,this);base.Close();if(active)returnTo?.Invoke();}

    internal static IEnumerable<(string Text,Action Pick)> Targets(EditorSession session,ConstructionSnapshot state,string series,int role,IEnumerable<DesignFact> facts,Action<int?> chosen,bool automatic=true,bool none=true)
    {
        if(automatic)yield return ("Automatic latest",()=>chosen(null));
        if(none)yield return ("No target",()=>chosen(ConstructionGraph.Disabled));
        foreach(var fact in facts.Where(f=>f.EmpireId==session.Empire.EmpireId&&f.Role==role&&f.Buildable&&state.DesignClasses.GetValueOrDefault(f.DesignId)==series).OrderByDescending(f=>f.StarDate).ThenByDescending(f=>f.DesignId))
        {
            int id=fact.DesignId;string name=session.Empire.Designs.GetById(id)?.Name??session.Draft.Name+" (after Save)";
            yield return (name+(fact.Obsolete?" - obsolete; becomes Active after Save":""),()=>chosen(id));
        }
    }
}
