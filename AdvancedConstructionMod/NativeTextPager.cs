using DistantWorlds.Types;
using DistantWorlds.UI;

namespace AdvancedConstructionMod;

internal sealed class NativeTextPager
{
    private readonly DWLabel label;
    private readonly DWButton previous,next;
    private string[] lines=[];
    private int page;
    private readonly float textHeight;
    private readonly bool topAligned,hideSinglePage;
    private string? text;
    private int Capacity=>Math.Max(1,(int)(textHeight/label.FontNormal.GetTotalLineSpacing(label.FontNormal.Size))-1);
    internal NativeTextPager(NativeWindow window,string name,float x,float y,float width,float height,float buttonHeight,bool topAligned=false,bool hideSinglePage=false)
    {
        this.topAligned=topAligned;this.hideSinglePage=hideSinglePage;textHeight=height-buttonHeight-4;
        label=window.Label(name,"",x,y,width,height-buttonHeight-4);
        previous=window.Button(name+".Previous","Previous details",x,y+height-buttonHeight,width*.48f,()=>{if(page>0){page--;Bind();}},buttonHeight);
        next=window.Button(name+".Next","Next details",x+width*.52f,y+height-buttonHeight,width*.48f,()=>{if((page+1)*Capacity<lines.Length){page++;Bind();}},buttonHeight);
    }
    internal void SetText(string text)
    {
        if(this.text==text)return;this.text=text;
        DrawingHelper.MeasureStringDropShadowWordWrapWithSize(text,label.FontNormal,label.Size.X-2*label.MarginScaled,out var wrapped);
        lines=wrapped.ToArray();page=0;Bind();
    }
    private void Bind()
    {
        var shown=lines.Skip(page*Capacity).Take(Capacity).ToArray();
        label.Text=string.Join('\n',shown);
        if(topAligned)label.Size=new(label.Size.X,Math.Min(textHeight,Math.Max(1,shown.Length)*label.FontNormal.GetTotalLineSpacing(label.FontNormal.Size)+2*label.MarginScaled));
        previous.Visible=next.Visible=!hideSinglePage||lines.Length>Capacity;previous.Enabled=page>0;next.Enabled=(page+1)*Capacity<lines.Length;
    }
}
