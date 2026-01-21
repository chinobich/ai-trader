import streamlit as st
import yfinance as yf
import pandas as pd
import pandas_ta as ta
from sklearn.ensemble import RandomForestClassifier
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime
import time
from linebot.v3.messaging import Configuration, ApiClient, MessagingApi, PushMessageRequest, TextMessage

# --- LINE設定 ---
LINE_TOKEN = st.secrets["LINE_CHANNEL_ACCESS_TOKEN"]
USER_ID = st.secrets["LINE_USER_ID"]

# --- ページ設定 ---
st.set_page_config(page_title="AI Terminal Pro", layout="wide", initial_sidebar_state="collapsed")

# --- カスタムCSS (Apple Dark Mode Style) ---
st.markdown("""
    <style>
    @import url('https://fonts.googleapis.com/css2?family=SF+Pro+Display:wght@400;600&display=swap');
    html, body, [data-testid="stAppViewContainer"] { background-color: #000000; font-family: 'SF Pro Display', sans-serif; }
    .stMetric { background: rgba(255, 255, 255, 0.05); border: 1px solid rgba(255, 255, 255, 0.1); border-radius: 20px !important; padding: 25px !important; }
    div.stButton > button { border-radius: 12px; height: 3.5rem; background-color: #007AFF; color: white; border: none; font-weight: 600; }
    .trade-log { background: #111; padding: 15px; border-radius: 12px; margin-bottom: 8px; border-left: 5px solid #007AFF; font-size: 0.9rem; }
    </style>
    """, unsafe_allow_html=True)

# --- 状態管理 ---
if 'active' not in st.session_state: st.session_state.active = False
if 'bank' not in st.session_state: st.session_state.bank = 1000000
if 'holding' not in st.session_state: st.session_state.holding = 0
if 'history_list' not in st.session_state: st.session_state.history_list = []

def send_line(msg):
    try:
        config = Configuration(access_token=LINE_TOKEN)
        with ApiClient(config) as client:
            api = MessagingApi(client)
            api.push_message(PushMessageRequest(to=USER_ID, messages=[TextMessage(text=msg)]))
    except Exception as e: print(f"LINE通知失敗: {e}")

# --- ヘッダー ---
st.markdown("<h1 style='font-weight:800; font-size: 2.5rem; margin-bottom:0;'>AI Terminal <span style='color:#007AFF;'>Pro</span></h1>", unsafe_allow_html=True)
st.markdown("<p style='color: #666; margin-bottom:2rem;'>Next-Gen Algorithmic Trading Interface</p>", unsafe_allow_html=True)

# --- コントロールパネル ---
with st.container():
    c1, c2, c3 = st.columns([2, 1, 2])
    with c1:
        target = st.text_input("SYMBOL", value="9509.T")
    with c2:
        if not st.session_state.active:
            if st.button("▶ START SYSTEM", use_container_width=True):
                st.session_state.active = True
                st.rerun()
        else:
            if st.button("⏹ STOP SYSTEM", use_container_width=True):
                st.session_state.active = False
                st.rerun()
    with c3:
        status_color = "#00FF00" if st.session_state.active else "#FF4B4B"
        st.markdown(f"<div style='text-align:right; font-size:1.2rem; font-weight:600; color:{status_color}'>{'● ONLINE' if st.session_state.active else '○ OFFLINE'}</div>", unsafe_allow_html=True)
        st.markdown(f"<div style='text-align:right; color:#444;'>{datetime.now().strftime('%H:%M:%S')}</div>", unsafe_allow_html=True)

st.write("")

if st.session_state.active:
    # データ取得と計算
    df = yf.download(target, period="5d", interval="1m", progress=False, auto_adjust=True)
    if isinstance(df.columns, pd.MultiIndex): df.columns = df.columns.get_level_values(0)
    
    # 指標計算
    df.ta.macd(append=True)
    df.ta.rsi(append=True)
    bb = ta.bbands(df['Close'], length=20)
    df = pd.concat([df, bb], axis=1)
    
    # カラム名の動的取得（エラー回避）
    bb_l = bb.columns[0] # Lower Band
    bb_m = bb.columns[1] # Mid Band
    bb_u = bb.columns[2] # Upper Band
    macd_h = [c for c in df.columns if 'MACDh' in c][0]

    # AI判定
    feats = ['Close', 'RSI_14']
    d_train = df.dropna().copy()
    d_train['Target'] = (d_train['Close'].shift(-1) > d_train['Close']).astype(int)
    model = RandomForestClassifier(n_estimators=100).fit(d_train[feats].iloc[:-1], d_train['Target'].iloc[:-1])
    pred = model.predict(df[feats].iloc[[-1]])[0]
    price = float(df['Close'].iloc[-1])

    # 売買ロジック
    if pred == 1 and st.session_state.holding == 0:
        st.session_state.holding = price
        st.session_state.history_list.append({"time": datetime.now(), "type": "BUY", "price": price})
        send_line(f"🚀【BUY】{target}\n価格: {price}円")
    elif pred == 0 and st.session_state.holding > 0:
        profit = (price - st.session_state.holding) * 100
        st.session_state.bank += profit
        st.session_state.history_list.append({"time": datetime.now(), "type": "SELL", "price": price, "profit": profit})
        st.session_state.holding = 0
        send_line(f"🤝【SELL】{target}\n価格: {price}円\n損益: {profit:+.1f}円")

    # メトリクス表示
    m1, m2, m3, m4 = st.columns(4)
    m1.metric("PORTFOLIO", f"{st.session_state.bank:,.0f} JPY", f"{st.session_state.bank-1000000:+.0f}")
    m2.metric("POSITION", f"{st.session_state.holding:,.1f}" if st.session_state.holding > 0 else "NONE")
    m3.metric("AI PREDICTION", "💹 BUY" if pred == 1 else "💤 WAIT")
    wins = [h for h in st.session_state.history_list if h.get('profit', 0) > 0]
    total_trades = len([h for h in st.session_state.history_list if 'profit' in h])
    wr = (len(wins) / total_trades) * 100 if total_trades > 0 else 0
    m4.metric("WIN RATE", f"{wr:.1f}%")

    # チャート描画
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.08, row_heights=[0.7, 0.3])
    fig.add_trace(go.Candlestick(x=df.index, open=df['Open'], high=df['High'], low=df['Low'], close=df['Close'], name="Price"), row=1, col=1)
    fig.add_trace(go.Scatter(x=df.index, y=df[bb_u], line=dict(color='rgba(0,122,255,0.2)', width=1), name="BB Upper"), row=1, col=1)
    fig.add_trace(go.Scatter(x=df.index, y=df[bb_l], line=dict(color='rgba(0,122,255,0.2)', width=1), fill='tonexty', name="BB Lower"), row=1, col=1)
    fig.add_trace(go.Bar(x=df.index, y=df[macd_h], name="MACD Hist"), row=2, col=1)
    
    fig.update_layout(height=650, template="plotly_dark", paper_bgcolor='black', plot_bgcolor='black', xaxis_rangeslider_visible=False, margin=dict(l=0,r=0,t=0,b=0))
    st.plotly_chart(fig, use_container_width=True, key=f"chart_{time.time()}")

    # 履歴
    st.markdown("### 📝 Recent Activity")
    for log in reversed(st.session_state.history_list[-5:]):
        color = "#007AFF" if log['type'] == "BUY" else "#FF9500"
        st.markdown(f"""<div class='trade-log' style='border-left-color:{color}'>
            <b>{log['time'].strftime('%H:%M')}</b> | {log['type']} at {log['price']} JPY 
            {f"<span style='float:right; color:#00FF00;'>Profit: {log['profit']:+.0f}</span>" if 'profit' in log else ''}
            </div>""", unsafe_allow_html=True)

    time.sleep(60)
    st.rerun()
else:
    st.markdown("<div style='padding: 100px; text-align: center; color: #333;'><h3>Terminal Standby</h3><p>STARTボタンを押してAI監視を開始してください</p></div>", unsafe_allow_html=True)