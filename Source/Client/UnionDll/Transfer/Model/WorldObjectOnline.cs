using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model
{
    [Serializable]
    public class WorldObjectOnline
    {
        public string Name { get; set; }
        public int Tile { get; set; }
        public string FactionGroup { get; set; }
        public string FactionDef { get; set; }
        public int loadID { get; set; }

        /// <summary>
        /// Объект создан серверным рассказчиком (а не прислан клиентом).
        /// </summary>
        public bool ServerGenerated { get; set; }

        /// <summary>
        /// Время удаления временного объекта (для лагерей). DateTime.MinValue для бессрочных.
        /// </summary>
        public DateTime ExpireAtUtc { get; set; }

        /// <summary>
        /// Тип сюжетного объекта: camp/trade_camp/outpost/settlement/...
        /// </summary>
        public string StoryType { get; set; }

        /// <summary>
        /// Уровень развития поселения рассказчика (1..N). Для не-поселений: 0.
        /// </summary>
        public int StoryLevel { get; set; }

        /// <summary>
        /// Время, до которого поселение не выполняет следующий шаг эволюции/экспансии.
        /// </summary>
        public DateTime StoryNextActionUtc { get; set; }

        /// <summary>
        /// Сид/идентификатор генерации точки рассказчика.
        /// </summary>
        public string StorySeed { get; set; }

    }
}
