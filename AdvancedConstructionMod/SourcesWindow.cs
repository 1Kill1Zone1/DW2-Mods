using DistantWorlds.Types;
using DistantWorlds.UI;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace AdvancedConstructionMod;

internal sealed class SourcesWindow : NativeWindow
{
    private readonly EditorSession session;
    private readonly Action changed;
    private readonly ScrollableList list=new();
    private readonly DWTextBox search;
    private readonly DWButton previewButton;
    private readonly List<SourceRow> rows=[];
    private readonly HashSet<int> selected=new(),original=new();
    private readonly bool targetBuildable;
    private bool onlyChanges;
    private readonly ListDrawableFieldList tableFields=new();
    private readonly NativeTextPager details;
    private SourceRow? selectedRow;
    private readonly float[] widths;
    private readonly Dictionary<int,string> proposedTargets=new();

    internal SourcesWindow(EditorSession session,Action changed):base("AdvancedConstructionSources","UPGRADES FROM — "+session.Draft.Name,1160,760)
    {
        this.session=session;this.changed=changed;Campaign=session.Galaxy;
        Panel.HeaderText="UPGRADES FROM";
        targetBuildable=RetrofitRuntime.Fact(session.Galaxy,session.Empire,session.Draft).Buildable;
        if(session.ClassId==null)throw new InvalidOperationException("Choose a Series first.");
        var counts=session.Galaxy.Ships.Where(s=>s!=null&&!s.IsDestroyed&&s.EmpireId==session.Empire.EmpireId).GroupBy(s=>s.DesignId).ToDictionary(g=>g.Key,g=>g.Count());
        var initialProjection=session.Projection();
        foreach(Design source in session.Empire.Designs.Where(d=>d!=null&&d.EmpireId==session.Empire.EmpireId&&d.DesignId!=session.Draft.DesignId&&d.GetRole(session.Galaxy)==session.Draft.GetRole(session.Galaxy)).OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase).ThenBy(d=>d.DesignId))
        {
            if(session.Published&&(source.UpgradeDesignId==session.Draft.DesignId||source.ResolveRetrofitDesignId(session.Galaxy,session.Empire)==session.Draft.DesignId))original.Add(source.DesignId);
            if(session.WillUpgradeHere(source,initialProjection))original.Add(source.DesignId);
            rows.Add(new SourceRow(this,source,counts.GetValueOrDefault(source.DesignId)));
        }
        if(session.Origin!=null && !rows.Any(r=>r.Design.DesignId==session.Origin.DesignId))
            rows.Add(new SourceRow(this,session.Origin,counts.GetValueOrDefault(session.Origin.DesignId)));
        // The preselected Upgrade origin must also retain an explicit uncheck.
        if(session.Origin!=null)original.Add(session.Origin.DesignId);
        selected.UnionWith(original);
        foreach(var choice in session.Sources){if(choice.Selected)selected.Add(choice.SourceDesignId);else selected.Remove(choice.SourceDesignId);}
        search=TextBox("ACM.SourceSearch",S(12),S(40),Panel.Size.X-S(328));
        Button("ACM.SourceSearchApply","Search",Panel.Size.X-S(304),S(40),S(90),Bind);
        previewButton=Button("ACM.SourcePreview","Show changes",Panel.Size.X-S(202),S(40),S(190),()=>{onlyChanges=!onlyChanges;Bind();});
        float available=Panel.Size.X-S(24),remaining=available-S(202);
        widths=[S(28),remaining*.27f,remaining*.21f,S(110),remaining*.26f,remaining*.26f,S(44)];
        Label("ACM.SourceTargetInfo","Project: "+session.Draft.Name+"\nSeries: "+session.ClassName+(session.Dirty?" (unsaved changes)":" (saved)"),S(12),S(74),available,S(44));
        string[] keys=["Select","Name","Series","Hull","Current","Proposed","Ships"],captions=["[ ]","Project","Series","Hull","Current target","After Save / Apply","Ships"];
        float x=S(12);
        for(int i=0;i<keys.Length;i++)
        {
            tableFields.Add(new(keys[i],widths[i]/S(1),true,4));
            Label("ACM.SourceHeader."+keys[i],captions[i],x,S(126),widths[i]-S(4),S(26));x+=widths[i];
        }
        list.Name="ACM.SourceList";list.PostScene=true;list.Initialize(FontSize.Normal,FontSize.Normal,null!);list.Visible=true;
        list.SetSizeAndPosition(new(available,Math.Max(S(60),Panel.Size.Y-S(452))),new(S(12),S(156)));list.IsSelectable=true;list.AllowMultiSelect=false;
        list.BindGalaxy(session.Galaxy,session.Empire);Panel.AddControl(list);
        details=new NativeTextPager(this,"ACM.SourceDetails",S(12),Panel.Size.Y-S(284),available,S(168),S(26));
        details.SetText("Select a project to see its full name and details.");
        Status=Label("ACM.SourceStatus","",S(12),Panel.Size.Y-S(108),available,S(64));
        Button("ACM.SourceApply",session.Published?"Apply":"Use choices",Panel.Size.X-S(264),Panel.Size.Y-S(40),S(120),Apply);
        Button("ACM.SourceCancel","Cancel",Panel.Size.X-S(132),Panel.Size.Y-S(40),S(120),Close);
        Bind();
    }
    private List<ConstructionChange> Changes()=>rows.Where(r=>selected.Contains(r.Design.DesignId)||original.Contains(r.Design.DesignId))
        .Select(r=>new ConstructionChange(r.Design.DesignId,session.Draft.DesignId,selected.Contains(r.Design.DesignId),original.Contains(r.Design.DesignId))).ToList();
    private bool IsChange(SourceRow row)=>selected.Contains(row.Design.DesignId)!=original.Contains(row.Design.DesignId)||
        (selected.Contains(row.Design.DesignId)&&(row.Design.UpgradeDesignId!=session.Draft.DesignId||!session.Classes.DesignClasses.ContainsKey(row.Design.DesignId)));
    private void Bind()
    {
        string query=search.Text?.Trim()??string.Empty;
        list.BindData(rows.Where(row=>(!onlyChanges||IsChange(row)) && (query.Length==0||row.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase))).Cast<IListDrawable>().ToList(),Clicked,null!,null!,tableFields);
        previewButton.Text=onlyChanges?"Show all projects":"Show changes";
        int links=rows.Count(r=>selected.Contains(r.Design.DesignId)),disabled=original.Count(id=>!selected.Contains(id));
        int adopts=rows.Count(r=>selected.Contains(r.Design.DesignId)&&!session.Classes.DesignClasses.ContainsKey(r.Design.DesignId));
        var projection=session.Projection(Changes());var preview=projection.Preview;
        proposedTargets.Clear();foreach(var row in rows)proposedTargets[row.Design.DesignId]=session.EffectiveTarget(row.Design,projection);
        if(selectedRow!=null)details.SetText(selectedRow.Text);
        list.SelectedItems=selectedRow!=null&&list.Items.Contains(selectedRow)?[selectedRow]:[];
        Message(preview.Accepted?$"Preview: {links} enabled source(s); {disabled} path(s) disabled; {adopts} source(s) adopt {session.ClassName}.\n"+(session.Published?"Apply publishes the After Save / Apply column. Ships stay in place.":"Use choices returns to the editor. Save publishes the proposed targets."):preview.Error,!preview.Accepted);
    }
    private void Clicked(IListDrawable item,string field,Vector2 ratio,bool left,bool add,ShipList ships,TroopList troops,List<IListDrawable> range)
    {
        if(!left||item is not SourceRow row)return;
        selectedRow=row;details.SetText(row.Text);
        if(field!="Select"&&field.Length!=0){list.SelectedItems=[row];return;}
        int id=row.Design.DesignId;
        if(selected.Contains(id))selected.Remove(id);
        else if(row.BlockReason!=null){Message(row.BlockReason,true);return;}
        else selected.Add(id);
        Bind();
    }
    private void Apply()
    {
        if(!ReferenceEquals(Active,this))return;
        if(!session.Fresh(out var error)){Message(error,true);return;}
        if(!session.ApplySources(Changes(),out error)){Message(error,true);return;}
        changed();Close();
    }
    private sealed class SourceRow : IListDrawable
    {
        private readonly SourcesWindow window;
        internal readonly Design Design;
        private readonly int built;
        internal SourceRow(SourcesWindow window,Design design,int built){this.window=window;Design=design;this.built=built;}
        private string ClassName=>window.session.Classes.DesignClasses.TryGetValue(Design.DesignId,out var id)&&window.session.Classes.Classes.TryGetValue(id,out var name)?name:"—";
        private string Hull=>Design.GetShipHullName(window.session.Galaxy);
        private string Target=>window.session.EffectiveTarget(Design);
        private string NativeTarget=>Design.UpgradeDesignId switch
        {
            int.MinValue=>"Disabled",-1=>"Latest, same hull",-2=>"Latest, same role",-3=>"Latest, largest hull",
            _=>window.session.Empire.Designs.GetById(Design.UpgradeDesignId)?.Name??("Unavailable #"+Design.UpgradeDesignId)
        };
        internal string? BlockReason=>!window.targetBuildable?"The target design is not currently valid/buildable with your researched hulls and components.":
            window.session.Classes.DesignClasses.TryGetValue(Design.DesignId,out var id)&&id!=window.session.ClassId?
            "This source belongs to another series. Change its series explicitly in its own editor first.":null;
        internal string SearchText=>$"{Design.Name} {ClassName} {Hull} {Target} #{Design.DesignId}";
        internal string Text=>$"{(window.selected.Contains(Design.DesignId)?"[x]":"[ ]")} {Design.Name}  [#{Design.DesignId}]  {(Design.Obsolete?"(obsolete)":"")}\nSeries: {ClassName} | Hull: {Hull} | Built ships: {built}\nCurrent: {Target} | Native path: {NativeTarget}\nAfter Apply: {window.proposedTargets.GetValueOrDefault(Design.DesignId,"Preview unavailable")}{(BlockReason!=null?" | Unavailable — "+BlockReason:"")}";
        public float DrawItem(RenderDrawContext context,SpriteBatch batch,DrawDetailType type,Point mouse,Vector2 position,float width,float gap,
            SpriteFont font,SpriteFont boldFont,SpriteFont majorFont,SpriteFont smallFont,Color color,Color shadow,float opacity,float saturation,float contrast,
            ListDrawableFieldList fields,Galaxy galaxy,Empire empire,object extra,ref HoverTipList tips,out GameTask task,out GameTask task2,out bool remove,out bool smallTask,bool considerInView)
        {
            task=null!;task2=null!;remove=false;smallTask=false;
            float spacing=font==null?16:font.GetTotalLineSpacing(font.Size),height=spacing+window.S(8);
            if(batch!=null&&font!=null&&considerInView)
            {
                string next=window.proposedTargets.GetValueOrDefault(Design.DesignId,"Preview unavailable");
                string[] cells=[window.selected.Contains(Design.DesignId)?"[x]":"[ ]",Design.Name,ClassName,Hull,Target,next,built.ToString()];
                float x=position.X;
                for(int i=0;i<cells.Length;i++)
                {
                    float y=position.Y;
                    foreach(string line in cells[i].Split('\n'))
                    {
                        string fit=DesignsSeriesColumn.Fit(line,font,window.widths[i]-window.S(8));
                        DrawingHelper.DrawStringDropShadow(batch,fit,font,BlockReason==null||ReferenceEquals(window.selectedRow,this)?color:Color.Gray,shadow,new(x+window.S(4),y));y+=spacing;
                    }
                    x+=window.widths[i];
                }
            }
            return height+gap;
        }
    }
}
